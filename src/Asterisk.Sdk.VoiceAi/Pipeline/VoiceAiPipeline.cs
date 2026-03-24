using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.Audio.Processing;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Events;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

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
    private volatile CancellationTokenSource? _ttsCts;
    private int _disposed;

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

        try
        {
            await Task.WhenAll(
                AudioMonitorLoop(session, utteranceChannel.Writer, ct),
                PipelineLoop(session, utteranceChannel.Reader, handler, history, ct)
            ).ConfigureAwait(false);
        }
        finally
        {
            VoiceAiLog.PipelineStopped(_logger, session.ChannelId);
            _state = PipelineState.Idle;
        }
    }

    private async Task AudioMonitorLoop(
        AudioSocketSession session,
        ChannelWriter<ReadOnlyMemory<byte>[]> utteranceWriter,
        CancellationToken ct)
    {
        var buffer = new List<ReadOnlyMemory<byte>>();
        var speechStartTime = DateTimeOffset.UtcNow;
        var silenceDuration = TimeSpan.Zero;
        var voiceDuration = TimeSpan.Zero;
        var utteranceDuration = TimeSpan.Zero;
        var isSpeaking = false;
        var frameDuration = TimeSpan.FromMilliseconds(20);

        try
        {
            await foreach (var frame in session.ReadAudioAsync(ct).ConfigureAwait(false))
            {
                var shortSpan = MemoryMarshal.Cast<byte, short>(frame.Span);
                var silence = AudioProcessor.IsSilence(shortSpan, _options.SilenceThresholdDb);

                if (_state == PipelineState.Speaking)
                {
                    if (!silence)
                    {
                        voiceDuration += frameDuration;
                        if (voiceDuration >= _options.BargInVoiceThreshold)
                        {
                            var ttsCts = _ttsCts;
                            ttsCts?.Cancel();
                            voiceDuration = TimeSpan.Zero;
                            VoiceAiLog.BargInDetected(_logger, session.ChannelId);
                            Publish(new BargInDetectedEvent(DateTimeOffset.UtcNow));

                            if (!isSpeaking)
                            {
                                isSpeaking = true;
                                buffer.Clear();
                                speechStartTime = DateTimeOffset.UtcNow;
                                utteranceDuration = TimeSpan.Zero;
                                Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                            }
                            buffer.Add(frame);
                            utteranceDuration += frameDuration;
                        }
                    }
                    else
                    {
                        voiceDuration = TimeSpan.Zero;
                    }
                    continue;
                }

                if (!silence)
                {
                    silenceDuration = TimeSpan.Zero;
                    if (!isSpeaking)
                    {
                        isSpeaking = true;
                        buffer.Clear();
                        speechStartTime = DateTimeOffset.UtcNow;
                        utteranceDuration = TimeSpan.Zero;
                        Publish(new SpeechStartedEvent(DateTimeOffset.UtcNow));
                    }
                    buffer.Add(frame);
                    utteranceDuration += frameDuration;

                    if (utteranceDuration >= _options.MaxUtteranceDuration)
                    {
                        await FlushUtterance(buffer, utteranceWriter, speechStartTime, ct).ConfigureAwait(false);
                        isSpeaking = false;
                        utteranceDuration = TimeSpan.Zero;
                        silenceDuration = TimeSpan.Zero;
                    }
                }
                else
                {
                    if (isSpeaking)
                    {
                        silenceDuration += frameDuration;
                        if (silenceDuration >= _options.EndOfUtteranceSilence)
                        {
                            await FlushUtterance(buffer, utteranceWriter, speechStartTime, ct).ConfigureAwait(false);
                            isSpeaking = false;
                            silenceDuration = TimeSpan.Zero;
                            utteranceDuration = TimeSpan.Zero;
                        }
                    }
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
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Stt, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex.Message, ex, PipelineErrorSource.Stt));
                _state = PipelineState.Listening;
                continue;
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

            _ttsCts = new CancellationTokenSource();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ttsCts.Token);
                await foreach (var audioChunk in _tts.SynthesizeAsync(
                    response, _options.OutputFormat, linked.Token).ConfigureAwait(false))
                {
                    await session.WriteAudioAsync(audioChunk, linked.Token).ConfigureAwait(false);
                }
                Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
                history.Add(new ConversationTurn(transcript, response, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Barge-in — _ttsCts was cancelled
                Publish(new SynthesisEndedEvent(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - synthStart));
            }
            catch (Exception ex)
            {
                VoiceAiLog.PipelineError(_logger, PipelineErrorSource.Tts, channelId, ex.Message);
                Publish(new PipelineErrorEvent(DateTimeOffset.UtcNow, ex.Message, ex, PipelineErrorSource.Tts));
            }
            finally
            {
                var ttsCts = _ttsCts;
                _ttsCts = null;
                ttsCts?.Dispose();
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

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _ttsCts?.Dispose();
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}
