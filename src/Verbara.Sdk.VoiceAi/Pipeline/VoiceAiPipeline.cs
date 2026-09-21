using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Diagnostics;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Orchestrates the Voice AI conversation loop: VAD → STT → handler → TTS,
/// with barge-in detection and error recovery.
/// </summary>
public sealed class VoiceAiPipeline : ISessionHandler, IAsyncDisposable
{
    private readonly SpeechRecognizer _stt;
    private readonly SpeechSynthesizer _tts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VoiceAiPipelineOptions _options;
    private readonly ILogger<VoiceAiPipeline> _logger;
    private readonly Subject<VoiceAiPipelineEvent> _events = new();

    private volatile PipelineState _state = PipelineState.Idle;
    private int _disposed;

    /// <summary>
    /// Guards <see cref="_ttsCts"/>. Every read, write, cancel and dispose of that field happens
    /// under this gate, and the field is set to <c>null</c> under the gate <em>before</em> the
    /// source is disposed outside it. That is what makes cancel-after-dispose unreachable rather
    /// than merely unlikely: the only reference anyone can obtain is one taken while the field was
    /// still live, and once the field is null nobody can obtain one at all.
    /// </summary>
    private readonly Lock _ttsGate = new();

    /// <summary>
    /// The synthesis in flight, or <c>null</c> between syntheses. Owned by <see cref="PipelineLoop"/>,
    /// which is the only member that creates or disposes it; everyone else may only ask it to cancel,
    /// through <see cref="CancelSynthesis"/>.
    /// </summary>
    private CancellationTokenSource? _ttsCts;

    /// <summary>Observable stream of pipeline lifecycle events.</summary>
    public IObservable<VoiceAiPipelineEvent> Events => _events;

    /// <summary>Creates a new pipeline instance.</summary>
    public VoiceAiPipeline(
        SpeechRecognizer stt,
        SpeechSynthesizer tts,
        IServiceScopeFactory scopeFactory,
        IOptions<VoiceAiPipelineOptions> options,
        ILogger<VoiceAiPipeline> logger)
    {
        _stt = stt;
        _tts = tts;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full pipeline for a single AudioSocket session.
    /// Returns when the session ends or the token is cancelled.
    /// </summary>
    public async ValueTask HandleSessionAsync(
        AudioSocketSession session,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IConversationHandler>();
        var turnDetector = scope.ServiceProvider.GetService<ITurnDetector>()
            ?? new SilenceTurnDetector(Options.Create(_options));

        VoiceAiMetrics.SessionsStarted.Add(1);
        var sessionStart = Stopwatch.GetTimestamp();
        using var sessionActivity = VoiceAiActivitySource.StartSession(session.ChannelId, handler.GetType().Name);

        VoiceAiLog.PipelineStarted(_logger, session.ChannelId);
        _state = PipelineState.Listening;

        var utteranceChannel = Channel.CreateBounded<ReadOnlyMemory<byte>[]>(
            new BoundedChannelOptions(4)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        var history = new List<ConversationTurn>();
        var failed = false;

        try
        {
            await Task.WhenAll(
                AudioMonitorLoop(session, utteranceChannel.Writer, turnDetector, ct),
                PipelineLoop(session, utteranceChannel.Reader, handler, history, ct)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ADR-0054: the caller asked for this, so it is how the session ended, not how it
            // failed — the classification OpenAiRealtimeBridge settled on in ADR-0053. Swallowed
            // rather than rethrown, because the bridge does not rethrow either and one
            // ISessionHandler interface has to answer one way.
        }
        catch
        {
            failed = true;
            VoiceAiMetrics.SessionsFailed.Add(1);
            sessionActivity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(sessionStart).TotalMilliseconds;
            VoiceAiMetrics.SessionDurationMs.Record(duration);
            if (!failed)
                VoiceAiMetrics.SessionsCompleted.Add(1);

            VoiceAiLog.PipelineStopped(_logger, session.ChannelId);
            _state = PipelineState.Idle;
        }
    }

    private async Task AudioMonitorLoop(
        AudioSocketSession session,
        ChannelWriter<ReadOnlyMemory<byte>[]> utteranceWriter,
        ITurnDetector turnDetector,
        CancellationToken ct)
    {
        var buffer = new List<ReadOnlyMemory<byte>>();
        var speechStartTime = DateTimeOffset.UtcNow;
        var isSpeaking = false;

        try
        {
            await foreach (var frame in session.ReadAudioAsync(ct).ConfigureAwait(false))
            {
                var shortSpan = MemoryMarshal.Cast<byte, short>(frame.Span);
                var signal = turnDetector.Analyze(shortSpan, _state == PipelineState.Speaking);

                switch (signal.Action)
                {
                    case TurnAction.SpeechStarted:
                        isSpeaking = true;
                        buffer.Clear();
                        speechStartTime = DateTimeOffset.UtcNow;
                        Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                        buffer.Add(frame);
                        break;

                    case TurnAction.EndOfUtterance:
                        await FlushUtterance(buffer, utteranceWriter, speechStartTime, ct).ConfigureAwait(false);
                        isSpeaking = false;
                        break;

                    case TurnAction.BargIn:
                        CancelSynthesis();
                        VoiceAiLog.BargInDetected(_logger, session.ChannelId);
                        Publish(new BargInDetectedEvent(DateTimeOffset.UtcNow));
                        if (!isSpeaking)
                        {
                            isSpeaking = true;
                            buffer.Clear();
                            speechStartTime = DateTimeOffset.UtcNow;
                            Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                        }
                        buffer.Add(frame);
                        break;

                    case TurnAction.Continue:
                        if (isSpeaking)
                            buffer.Add(frame);
                        break;
                }
            }
        }
        finally
        {
            utteranceWriter.Complete();
        }
    }

    private async Task FlushUtterance(
        List<ReadOnlyMemory<byte>> buffer,
        ChannelWriter<ReadOnlyMemory<byte>[]> writer,
        DateTimeOffset speechStartTime,
        CancellationToken ct)
    {
        var captured = buffer.ToArray();
        buffer.Clear();
        var duration = DateTimeOffset.UtcNow - speechStartTime;
        Publish(new SpeechEndedEvent(DateTimeOffset.UtcNow, duration));
        await writer.WriteAsync(captured, ct).ConfigureAwait(false);
    }

    private async Task PipelineLoop(
        AudioSocketSession session,
        ChannelReader<ReadOnlyMemory<byte>[]> utteranceReader,
        IConversationHandler handler,
        List<ConversationTurn> history,
        CancellationToken ct)
    {
        var channelId = session.ChannelId;

        await foreach (var utterance in utteranceReader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            _state = PipelineState.Recognizing;

            SpeechRecognitionMetrics.TranscriptionsStarted.Add(1);
            var sttStart = Stopwatch.GetTimestamp();
            using var sttActivity = VoiceAiActivitySource.StartRecognition(_stt.ProviderName);

            string? transcript = null;
            try
            {
                await foreach (var result in _stt.StreamAsync(
                    ToAsyncEnumerable(utterance, ct), _options.InputFormat, ct).ConfigureAwait(false))
                {
                    Publish(new TranscriptReceivedEvent(
                        DateTimeOffset.UtcNow, result.Transcript, result.Confidence, result.IsFinal));
                    if (result.IsFinal)
                        transcript = result.Transcript;
                }
                SpeechRecognitionMetrics.TranscriptionsCompleted.Add(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SpeechRecognitionMetrics.TranscriptionsFailed.Add(1);
                sttActivity?.SetStatus(ActivityStatusCode.Error);
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Stt, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex.Message, ex, PipelineErrorSource.Stt));
                _state = PipelineState.Listening;
                continue;
            }
            finally
            {
                SpeechRecognitionMetrics.TranscriptionLatencyMs.Record(
                    Stopwatch.GetElapsedTime(sttStart).TotalMilliseconds);
            }

            if (transcript is null)
            {
                _state = PipelineState.Listening;
                continue;
            }

            _state = PipelineState.Handling;
            string? response = null;
            try
            {
                var trimmedHistory = history.Count > _options.MaxHistoryTurns
                    ? history.Skip(history.Count - _options.MaxHistoryTurns).ToList()
                    : history;

                var context = new ConversationContext
                {
                    ChannelId = channelId,
                    History = trimmedHistory,
                    InputFormat = _options.InputFormat
                };
                response = await handler.HandleAsync(transcript, context, ct).ConfigureAwait(false);
                Publish(new ResponseGeneratedEvent(DateTimeOffset.UtcNow, response));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Handler, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex.Message, ex, PipelineErrorSource.Handler));
                _state = PipelineState.Listening;
                continue;
            }

            _state = PipelineState.Speaking;
            var synthStart = DateTimeOffset.UtcNow;
            Publish(new SynthesisStartedEvent(synthStart));

            SpeechSynthesisMetrics.SynthesesStarted.Add(1);
            SpeechSynthesisMetrics.SynthesisCharacters.Add(response.Length);
            var ttsStart = Stopwatch.GetTimestamp();
            using var ttsActivity = VoiceAiActivitySource.StartSynthesis(_tts.ProviderName, response.Length);

            // Disposed when this iteration's block ends: after the finally below has unpublished it
            // under the gate, which is the order _ttsGate documents.
            using var ttsCts = new CancellationTokenSource();
            lock (_ttsGate)
            {
                // Publishing the source and observing an already-disposed pipeline have to happen
                // under one gate, or a DisposeAsync landing here would cancel nothing and leave this
                // synthesis running past the disposal that was meant to stop it.
                if (Volatile.Read(ref _disposed) != 0)
                    ttsCts.Cancel();
                _ttsCts = ttsCts;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ttsCts.Token);
                var ttfaRecorded = false;
                var farEndGone = false;
                await foreach (var audioChunk in _tts.SynthesizeAsync(
                    response, _options.OutputFormat, linked.Token).ConfigureAwait(false))
                {
                    if (!ttfaRecorded)
                    {
                        SpeechSynthesisMetrics.SynthesisTtfaMs.Record(
                            Stopwatch.GetElapsedTime(ttsStart).TotalMilliseconds,
                            new KeyValuePair<string, object?>("voiceai.provider", _tts.ProviderName));
                        // TODO(R1.5+): expose tts.model tag when SpeechSynthesizer exposes a Model property (non-breaking additive virtual property).
                        ttfaRecorded = true;
                    }

                    try
                    {
                        await session.WriteAudioAsync(audioChunk, linked.Token).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // ADR-0057: the write found the session already gone. For this sealed session
                        // type that can only mean its own teardown has run — the guard at
                        // AudioSocketSession.cs:118 reads a flag none but that private terminate sets
                        // (:232), and the transport a flush would touch is what the same terminate
                        // closes last (:236). Nobody in the process asked for that ending, so it
                        // cancels neither token and neither filter below can see it: it is ADR-0053 R3's
                        // ending seen from the write side, not a fault of the synthesizer.
                        //
                        // Scoped to this one call deliberately. An ObjectDisposedException raised by the
                        // *synthesizer* — a provider client used after its own disposal — still reaches
                        // the failure clause it belongs to, and a transport that breaks while the session
                        // is still connected surfaces as IOException/SocketException, is not caught here,
                        // and stays a synthesis failure.
                        farEndGone = true;
                        break;
                    }
                }

                if (farEndGone)
                {
                    // Leaving the enumeration above disposed the provider sequence rather than
                    // draining an answer nobody can hear.
                    //
                    // Accounted exactly as the barge-in clause below accounts its ending: both are
                    // someone outside the pipeline ending this playback, and tying the two together
                    // means one future instrument separating "the caller heard the answer" from "the
                    // caller heard part of it" moves both at once. Counting it completed is today's
                    // accounting, not a claim the caller heard the answer (ADR-0050 E9 debt).
                    //
                    // Not the normal-completion tail below: that one adds the turn to the conversation
                    // history and guards tts.syntheses.silent, and neither belongs to a turn whose
                    // listener has gone. The ending is visible at Debug and nowhere an operator pages.
                    VoiceAiLog.PlaybackStoppedSessionEnded(_logger, channelId);
                    SpeechSynthesisMetrics.SynthesesCompleted.Add(1);
                    Publish(new SynthesisEndedEvent(
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
                }
                else
                {
                    // ADR-0050 E9, and the whole reason it is additive: reaching this line means the
                    // synthesizer finished, was not cancelled and threw nothing, so the eight WebSocket
                    // clients in this SDK cannot arrive here empty — they raise
                    // SpeechProviderEmptyResultException and land in the catch below. What can arrive here
                    // empty is the residual: an HTTP-backed synthesizer, or any third-party subclass of the
                    // public base, returning silence in silence. `ttfaRecorded` is the flag to test because
                    // it is set exactly once, on the first chunk yielded.
                    //
                    // Only the provider is tagged. The two-type discriminator that E8 substitutes for D2's
                    // is not a variable at this site — nothing was thrown here, which is precisely what
                    // makes the sample worth taking.
                    if (!ttfaRecorded)
                    {
                        SpeechSynthesisMetrics.SynthesesSilent.Add(1,
                            new KeyValuePair<string, object?>("voiceai.provider", _tts.ProviderName));
                    }

                    SpeechSynthesisMetrics.SynthesesCompleted.Add(1);
                    Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
                    history.Add(new ConversationTurn(transcript, response, DateTimeOffset.UtcNow));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (ttsCts.IsCancellationRequested)
            {
                // A requested ending: a barge-in or a disposal. Nothing else cancels this source, and
                // both reach it only through CancelSynthesis or, when the pipeline was already
                // disposed, at publication above. It is still undisposed here: it is released when
                // this iteration's block ends, after the finally below.
                //
                // The filter reads the source rather than comparing ex.CancellationToken. A synthesizer
                // is handed the linked token, never ttsCts.Token, and one that links sources of its own
                // raises whichever token it observed, so comparing tokens would miss a genuine barge-in
                // (ADR-0053 records the same trap for the bridge's ConnectAsync).
                //
                // Counting it completed is today's accounting, not a claim that the caller heard the
                // answer: ADR-0050 E9 records it as debt.
                SpeechSynthesisMetrics.SynthesesCompleted.Add(1);
                Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
            }
            catch (Exception ex)
            {
                // Also reached by an OperationCanceledException that neither filter above explains:
                // the synthesizer cancelled itself (an HttpClient.Timeout, a connect deadline it set)
                // while nobody asked it to stop, so the synthesis failed. ADR-0050 E6/E8 make the
                // tokens the pipeline holds the discriminator, not the exception's shape or token.
                //
                // The filters run only once await foreach has awaited the enumerator's DisposeAsync,
                // so a barge-in, a disposal or the caller's cancellation that lands while such a
                // cancellation is still unwinding is visible to them, and wins.
                SpeechSynthesisMetrics.SynthesesFailed.Add(1);
                ttsActivity?.SetStatus(ActivityStatusCode.Error);
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Tts, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex.Message, ex, PipelineErrorSource.Tts));
            }
            finally
            {
                SpeechSynthesisMetrics.SynthesisLatencyMs.Record(
                    Stopwatch.GetElapsedTime(ttsStart).TotalMilliseconds);
                lock (_ttsGate)
                    _ttsCts = null;
            }

            _state = PipelineState.Listening;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(
        ReadOnlyMemory<byte>[] frames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return frame;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Publish(VoiceAiPipelineEvent evt) => _events.OnNext(evt);

    /// <summary>
    /// Asks the synthesis in flight, if any, to stop. This is the only way anything other than
    /// <see cref="PipelineLoop"/> may touch <see cref="_ttsCts"/>.
    /// </summary>
    private void CancelSynthesis()
    {
        lock (_ttsGate)
            _ttsCts?.Cancel();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        // Intent, not mechanism (ADR-0053's shape, ADR-0054 for this type): a disposed pipeline
        // wants the synthesis stopped, but PipelineLoop's finally is what releases the source.
        CancelSynthesis();

        // Completed, deliberately not disposed. The two loops outlive this call — Task.WhenAll has
        // not observed them yet — and they publish as they unwind. OnCompleted has already dropped
        // every observer, so those publishes are silent no-ops; Dispose would instead make each one
        // throw ObjectDisposedException, which is the very defect this change exists to remove.
        _events.OnCompleted();
        return ValueTask.CompletedTask;
    }
}
