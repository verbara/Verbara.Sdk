using System.Diagnostics;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Diagnostics;
using Verbara.Sdk.VoiceAi.Events;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Pins how a <see cref="VoiceAiPipeline"/> session is classified when it ends for a reason nobody
/// called a fault (the caller cancelled the token, or the caller disposed the pipeline while the
/// assistant was still speaking), and how a synthesis is classified when a cancellation ends it.
/// </summary>
/// <remarks>
/// <para>
/// Both endings used to arrive at the bare <c>catch</c> that wraps the two loops, which counted a
/// failure and rethrew. <c>ADR-0054</c> settles the disagreement this created with
/// <c>OpenAiRealtimeBridge</c> (<c>ADR-0053</c>): one <c>ISessionHandler</c> interface reports one
/// number, and a requested ending is a completion.
/// </para>
/// <para>
/// A synthesis is classified by the same rule one level down: by whose cancellation ended it. The
/// caller's token and the synthesis's own source (cancelled by a barge-in or a disposal) are requested
/// endings, and a requested ending is not a synthesis failure. A synthesizer that cancels itself while
/// neither is cancelled has failed, and used to be booked as a barge-in (<c>ADR-0050</c> E6/E8: the
/// token is the discriminator). What a requested ending counts as instead is today's accounting, which
/// <c>ADR-0050</c> E9 records as debt; the tests pin it so that changing it is a decision.
/// </para>
/// <para>
/// Nothing here waits on a clock to establish an ordering. The synthesizer parks between chunks, or in
/// its enumerator's <c>DisposeAsync</c> after cancelling itself, and says so; the turn detector — which
/// the pipeline calls synchronously, one call per frame — says which frame it just decided on. Every
/// step is therefore ordered by construction, which is the whole point: a race test built on a delay
/// would reintroduce the defect class <c>ADR-0045</c> and <c>ADR-0053</c> exist about.
/// </para>
/// </remarks>
[Collection(SessionCounterGroup.Name)]
public sealed class VoiceAiPipelineCancellationAccountingTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi";

    /// <summary>
    /// The synthesis counters are untagged process-wide statics too, and only a pipeline emits them,
    /// so <see cref="SessionCounterGroup"/> isolates them the same way.
    /// </summary>
    private const string TtsMeterName = "Verbara.Sdk.VoiceAi.Tts";

    [Fact]
    public async Task HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenTheCallerCancels()
    {
        // Arrange — the session has nothing to do but read frames, so the only thing that can end it
        // is the token.
        var detector = new ScriptedTurnDetector();
        await using var tts = new ParkingSpeechSynthesizer();
        await using var pipeline = BuildPipeline(tts, detector);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var metrics = new MeterCapture(MeterName);
        using var cts = new CancellationTokenSource();

        // Act — the cancelled token goes to the subject and nowhere else (ADR-0052 F3)
        var sessionTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();
        await client.SendAudioAsync(SilenceFrame());
        await detector.FirstAnalyzed.WaitAsync(SignalTimeout);   // both loops are demonstrably running
        await cts.CancelAsync();

        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert — all of it in one scope, so a pre-fix run reports the whole defect at once
        using (new AssertionScope())
        {
            fault.Should().BeNull("a cancellation the caller asked for is not a fault");
            metrics.Get("voiceai.sessions.started").Should().Be(1);
            metrics.Get("voiceai.sessions.completed").Should()
                .Be(1, "the terminal block must run wherever the cancel landed");
            metrics.Get("voiceai.sessions.failed").Should().Be(0);
            metrics.GetDouble("voiceai.session.duration_ms").Should().BeGreaterThan(0);
        }

        await CleanupAsync(client, server);
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldEndCleanlyAndStayAccountedFor_WhenABargeInFollowsDisposal()
    {
        // Arrange — three frames drive the whole session: speech, end of utterance, barge-in. The
        // synthesizer parks after its first chunk, so the barge-in lands while a synthesis is live.
        var detector = new ScriptedTurnDetector(
            TurnAction.SpeechStarted, TurnAction.EndOfUtterance, TurnAction.BargIn);
        await using var tts = new ParkingSpeechSynthesizer();
        var pipeline = BuildPipeline(tts, detector);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var metrics = new MeterCapture(MeterName);

        var sessionTask = pipeline.HandleSessionAsync(session, CancellationToken.None).AsTask();

        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);

        // The synthesizer has yielded and parked, so `_ttsCts` is assigned and PipelineLoop's
        // `finally` has not run. Both facts are established by a signal, not by elapsed time.
        await tts.Parked.WaitAsync(SignalTimeout);

        // Act — dispose while the assistant is mid-sentence, then barge in on top of it.
        await pipeline.DisposeAsync();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(2).WaitAsync(SignalTimeout);

        // Let the parked synthesis go, so the session can finish however it is going to finish.
        // Pre-fix the barge-in has already thrown out of AudioMonitorLoop by now; releasing is what
        // stops Task.WhenAll waiting forever on the loop that is still parked.
        tts.Release();
        await SendHangupToleratingATornDownSessionAsync(client);

        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull(
                "a barge-in is a feature working; disposing the pipeline must not turn it into a throw");
            metrics.Get("voiceai.sessions.failed").Should()
                .Be(0, "nothing failed — the caller barged in and the caller disposed");
            metrics.Get("voiceai.sessions.completed").Should().Be(1);
        }

        await CleanupAsync(client, server);
    }

    // ---- A synthesis ended by a cancellation: whose cancellation it was decides what it counts as ----

    /// <summary>The shapes a synthesizer's own cancellation takes when nobody asked it to stop.</summary>
    public enum OwnCancellation
    {
        /// <summary>
        /// What an elapsed <c>HttpClient.Timeout</c> raises: a <see cref="TaskCanceledException"/> whose
        /// inner exception is a <see cref="TimeoutException"/>, carrying the token of a source the
        /// synthesizer owns and has cancelled.
        /// </summary>
        HttpClientTimeout,

        /// <summary>
        /// A bare <see cref="OperationCanceledException"/>, which any implementation of the public base
        /// is free to raise from a deadline of its own.
        /// </summary>
        PlainOperationCanceled,
    }

    /// <summary>The cancellations that end a synthesis because somebody asked it to stop.</summary>
    public enum RequestedEnding
    {
        /// <summary>The caller speaks over the assistant, which cancels the synthesis's own source.</summary>
        BargIn,

        /// <summary>The pipeline is disposed, which cancels the same source a barge-in does.</summary>
        PipelineDisposal,

        /// <summary>The caller cancels the token it handed the session.</summary>
        CallerToken,
    }

    /// <summary>
    /// A synthesizer that cancels itself, while nobody the pipeline answers to asked it to stop, has
    /// failed: the caller heard nothing, or heard the answer cut off. It is reported the way every
    /// other synthesis failure is, instead of being booked as the barge-in the unfiltered <c>catch</c>
    /// assumed every non-caller cancellation to be.
    /// </summary>
    /// <remarks>
    /// Two real inputs were measured reaching that <c>catch</c> with the caller's token live, and both
    /// were a <see cref="TaskCanceledException"/> carrying a cancelled token the pipeline never held:
    /// an elapsed <c>HttpClient.Timeout</c>, and <c>DeepgramSpeechSynthesizer</c>'s own connect
    /// deadline. The <see cref="OwnCancellation.HttpClientTimeout"/> shape carries such a token too, so
    /// a filter that also trusted the exception's token fails here. The plain shape stands for any
    /// other implementation of the public base. Each shape is raised before any audio and after one
    /// chunk has been written to the session, so a filter that asked whether audio had already been
    /// played fails here.
    /// </remarks>
    [Theory]
    [InlineData(OwnCancellation.HttpClientTimeout, 0)]
    [InlineData(OwnCancellation.PlainOperationCanceled, 0)]
    [InlineData(OwnCancellation.HttpClientTimeout, 1)]
    [InlineData(OwnCancellation.PlainOperationCanceled, 1)]
    public async Task HandleSessionAsync_ShouldPublishTtsPipelineError_WhenSynthesizerCancelsOnItsOwn(
        OwnCancellation shape, int chunksBeforeCancelling)
    {
        // Arrange — the synthesizer yields its chunks, cancels a source of its own, then throws from
        // the MoveNextAsync after them. The pipeline writes each chunk to the session before it asks
        // for the next one.
        var detector = new ScriptedTurnDetector(TurnAction.SpeechStarted, TurnAction.EndOfUtterance);
        await using var tts = new SelfCancellingSpeechSynthesizer(
            ownToken => OwnCancellationOf(shape, ownToken), chunksBeforeCancelling);
        var logger = new RecordingLogger();
        await using var pipeline = BuildPipeline(tts, detector, logger);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var activities = new SynthesisActivityRecorder();
        using var capture = new PipelineEventCapture(pipeline);

        // Act — no caller token at all, no barge-in and no disposal.
        var sessionTask = pipeline.HandleSessionAsync(session, CancellationToken.None).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);

        // Either ending of the response cycle completes this wait, so an unfixed pipeline fails on
        // the assertions below rather than on the bound.
        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);

        await client.SendHangupAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert — after the session ended, so the synthesis activity has already stopped.
        using (new AssertionScope())
        {
            // The premise a filter that trusted the exception's token trips on: the measured shape
            // carries a token its synthesizer cancelled, and the plain shape carries none.
            tts.OwnCancellation.CancellationToken.IsCancellationRequested.Should()
                .Be(shape == OwnCancellation.HttpClientTimeout);

            fault.Should().BeNull("a failed synthesis is reported, not rethrown");
            capture.Events.OfType<PipelineErrorEvent>().Should().ContainSingle(
                e => e.Source == PipelineErrorSource.Tts && ReferenceEquals(e.Exception, tts.OwnCancellation),
                "the synthesizer cancelled itself while nobody had asked it to stop");
            capture.Events.OfType<SynthesisEndedEvent>().Should()
                .BeEmpty("a synthesis that failed did not end");
            ttsMetrics.Get("tts.syntheses.started").Should().Be(1);
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(0);
            ttsMetrics.Get("tts.syntheses.failed").Should().Be(1);
            ttsMetrics.Get("tts.syntheses.silent").Should()
                .Be(0, "only a synthesis that completes can be counted silent");
            (ttsMetrics.GetDouble("tts.synthesis.ttfa_ms") > 0).Should().Be(
                chunksBeforeCancelling > 0, "time to first audio is recorded exactly when audio was yielded");
            ttsMetrics.GetDouble("tts.synthesis.latency_ms").Should().BeGreaterThan(0);
            sessionMetrics.Get("voiceai.sessions.failed").Should()
                .Be(0, "a failed synthesis fails its turn, not the session");
            sessionMetrics.Get("voiceai.sessions.completed").Should().Be(1);
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && e.Message.Contains("[Tts]", StringComparison.Ordinal));
            activities.Statuses.Should().Equal(ActivityStatusCode.Error);
        }

        await CleanupAsync(client, server);
    }

    /// <summary>
    /// A synthesis that failed because the synthesizer cancelled itself fails its turn, not the
    /// conversation: the pipeline goes on to recognise, handle and synthesise the caller's next
    /// utterance.
    /// </summary>
    /// <remarks>
    /// The synthesizer cancels only its first synthesis and answers the second, so the second response
    /// cycle ends only if the loop went on past the failure.
    /// </remarks>
    [Fact]
    public async Task HandleSessionAsync_ShouldAnswerTheNextUtterance_WhenSynthesizerCancelsOnItsOwn()
    {
        // Arrange — two utterances, each a speech frame followed by an end-of-utterance frame.
        var detector = new ScriptedTurnDetector(
            TurnAction.SpeechStarted, TurnAction.EndOfUtterance,
            TurnAction.SpeechStarted, TurnAction.EndOfUtterance);
        await using var tts = new SelfCancellingSpeechSynthesizer(
            ownToken => OwnCancellationOf(OwnCancellation.HttpClientTimeout, ownToken),
            chunksBeforeCancelling: 0);
        await using var pipeline = BuildPipeline(tts, detector);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var capture = new PipelineEventCapture(pipeline);

        var sessionTask = pipeline.HandleSessionAsync(session, CancellationToken.None).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);
        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);

        // Act — the caller speaks again after the failed synthesis.
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(2).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(3).WaitAsync(SignalTimeout);

        // A loop that stopped after the failure never ends a second cycle. The wait is recorded rather
        // than thrown, so that case is reported together with the counters below.
        var secondCycle = await Record.ExceptionAsync(
            () => capture.WaitForResponseCycle(2).WaitAsync(SignalTimeout));

        await client.SendHangupAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            secondCycle.Should().BeNull("the pipeline goes on to the caller's next utterance");
            fault.Should().BeNull("a failed synthesis is reported, not rethrown");
            capture.Events.OfType<TranscriptReceivedEvent>().Should()
                .HaveCount(2, "both utterances were recognised");
            capture.Events.OfType<ResponseGeneratedEvent>().Should()
                .HaveCount(2, "both transcripts were handled");
            ttsMetrics.Get("tts.syntheses.started").Should().Be(2, "both responses were synthesised");
            ttsMetrics.Get("tts.syntheses.failed").Should().Be(1);
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(1, "nothing cancelled the second synthesis");
            capture.Events.OfType<PipelineErrorEvent>().Should().ContainSingle(
                e => e.Source == PipelineErrorSource.Tts && ReferenceEquals(e.Exception, tts.OwnCancellation));
            capture.Events.OfType<SynthesisEndedEvent>().Should().ContainSingle();
            sessionMetrics.Get("voiceai.sessions.failed").Should().Be(0);
            sessionMetrics.Get("voiceai.sessions.completed").Should().Be(1);
        }

        await CleanupAsync(client, server);
    }

    /// <summary>
    /// The first control, and the half that keeps the fix from over-correcting: a barge-in cancels the
    /// synthesis's own source, so it stays a requested ending and is not a synthesis failure.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenABargeInCancelsIt()
    {
        // Arrange — speech, end of utterance, then a barge-in while the synthesizer is parked.
        var detector = new ScriptedTurnDetector(
            TurnAction.SpeechStarted, TurnAction.EndOfUtterance, TurnAction.BargIn);
        await using var tts = new ParkingSpeechSynthesizer();
        var logger = new RecordingLogger();
        await using var pipeline = BuildPipeline(tts, detector, logger);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var activities = new SynthesisActivityRecorder();
        using var capture = new PipelineEventCapture(pipeline);

        var sessionTask = pipeline.HandleSessionAsync(session, CancellationToken.None).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);
        await tts.Parked.WaitAsync(SignalTimeout);

        // Act — the caller speaks over the assistant.
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(2).WaitAsync(SignalTimeout);
        await capture.WaitForResponseCycle().WaitAsync(SignalTimeout);

        await client.SendHangupAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull();
            capture.Events.OfType<BargInDetectedEvent>().Should().ContainSingle();
            capture.Events.OfType<PipelineErrorEvent>().Should()
                .BeEmpty("a barge-in is a feature working, not a failed synthesis");
            ttsMetrics.Get("tts.syntheses.failed").Should().Be(0);
            sessionMetrics.Get("voiceai.sessions.failed").Should().Be(0);
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            activities.Statuses.Should().ContainSingle().Which.Should().NotBe(ActivityStatusCode.Error);

            // Today's accounting, pinned so that changing it is a decision; the requirement does not
            // ask for it. ADR-0050 E9 records counting a cancelled synthesis as completed as debt.
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(1);
            capture.Events.OfType<SynthesisEndedEvent>().Should().ContainSingle();
        }

        await CleanupAsync(client, server);
    }

    /// <summary>
    /// The second control: disposing the pipeline mid-synthesis cancels the same source a barge-in
    /// does, so it is a requested ending too. Its events are no witness: disposal completes the event
    /// stream, and whether the synthesis publishes before that depends on where the cancellation's
    /// continuation runs, so the counters, the log and the activity carry the assertion.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenDisposalCancelsIt()
    {
        // Arrange
        var detector = new ScriptedTurnDetector(TurnAction.SpeechStarted, TurnAction.EndOfUtterance);
        await using var tts = new ParkingSpeechSynthesizer();
        var logger = new RecordingLogger();
        var pipeline = BuildPipeline(tts, detector, logger);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var activities = new SynthesisActivityRecorder();

        var sessionTask = pipeline.HandleSessionAsync(session, CancellationToken.None).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);
        await tts.Parked.WaitAsync(SignalTimeout);

        // Act — nothing releases the parked synthesis before the assertions, so the session can only
        // finish once the cancellation raised by this disposal has run through PipelineLoop's catch.
        await pipeline.DisposeAsync();
        await client.SendHangupAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull();
            ttsMetrics.Get("tts.syntheses.failed").Should()
                .Be(0, "disposing the pipeline asked the synthesis to stop");
            sessionMetrics.Get("voiceai.sessions.failed").Should().Be(0);
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            activities.Statuses.Should().ContainSingle().Which.Should().NotBe(ActivityStatusCode.Error);

            // Today's accounting, not a requirement (ADR-0050 E9 debt, as for a barge-in).
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(1);
        }

        await CleanupAsync(client, server);
    }

    /// <summary>
    /// The third control: the caller's own cancellation during a synthesis belongs to the caller. The
    /// loop rethrows it and the session counts itself completed (<c>ADR-0054</c> R1), so it is not a
    /// synthesis failure either.
    /// </summary>
    [Fact]
    public async Task HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenTheCallerCancelsIt()
    {
        // Arrange
        var detector = new ScriptedTurnDetector(TurnAction.SpeechStarted, TurnAction.EndOfUtterance);
        await using var tts = new ParkingSpeechSynthesizer();
        var logger = new RecordingLogger();
        await using var pipeline = BuildPipeline(tts, detector, logger);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var activities = new SynthesisActivityRecorder();
        using var capture = new PipelineEventCapture(pipeline);
        using var cts = new CancellationTokenSource();

        var sessionTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);
        await tts.Parked.WaitAsync(SignalTimeout);

        // Act — the cancelled token goes to the subject and nowhere else (ADR-0052 F3). Nothing
        // releases the parked synthesis, so the session can only finish through this cancellation.
        await cts.CancelAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeNull("a cancellation the caller asked for is not a fault");
            capture.Events.OfType<PipelineErrorEvent>().Should().BeEmpty();
            ttsMetrics.Get("tts.syntheses.failed").Should()
                .Be(0, "the caller asked the synthesis to stop");
            sessionMetrics.Get("voiceai.sessions.failed").Should().Be(0);
            sessionMetrics.Get("voiceai.sessions.completed").Should().Be(1);
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            activities.Statuses.Should().ContainSingle().Which.Should().NotBe(ActivityStatusCode.Error);

            // Today's accounting, not a requirement: the caller's clause counts the synthesis as
            // neither completed nor failed, and publishes nothing for it.
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(0);
            capture.Events.OfType<SynthesisEndedEvent>().Should().BeEmpty();
        }

        await CleanupAsync(client, server);
    }

    /// <summary>
    /// A requested ending outranks the synthesizer's own cancellation when both are visible: a
    /// barge-in, a disposal or the caller's cancellation that lands while that cancellation is still
    /// unwinding is not reported as a synthesis failure.
    /// </summary>
    /// <remarks>
    /// The race is ordered by a seam, not by a sleep. <c>await foreach</c> awaits the enumerator's
    /// <c>DisposeAsync</c> before the exception reaches <c>PipelineLoop</c>'s catch filters, because an
    /// <c>await</c> in a <c>finally</c> is lowered as a catch, the await and a rethrow. The synthesizer
    /// throws its own cancellation from <c>MoveNextAsync</c> and parks in <c>DisposeAsync</c>; the
    /// test lands the requested ending there, proves it has cancelled what it cancels, and only then
    /// lets the synthesizer go. The exception carries an inner <see cref="TimeoutException"/>, so a
    /// filter that also asked what the exception looks like fails here.
    /// </remarks>
    [Theory]
    [InlineData(RequestedEnding.BargIn, 1L)]
    [InlineData(RequestedEnding.PipelineDisposal, 1L)]
    [InlineData(RequestedEnding.CallerToken, 0L)]
    public async Task HandleSessionAsync_ShouldNotReportASynthesisFailure_WhenARequestedEndingLandsWhileTheOwnCancellationUnwinds(
        RequestedEnding ending, long completedToday)
    {
        // Arrange
        var detector = new ScriptedTurnDetector(
            TurnAction.SpeechStarted, TurnAction.EndOfUtterance, TurnAction.BargIn);
        await using var tts = new UnwindingSelfCancellingSpeechSynthesizer();
        var logger = new RecordingLogger();
        await using var pipeline = BuildPipeline(tts, detector, logger);
        var (session, server, client) = await CreateAudioSessionAsync();
        using var sessionMetrics = new MeterCapture(MeterName);
        using var ttsMetrics = new MeterCapture(TtsMeterName);
        using var activities = new SynthesisActivityRecorder();
        using var capture = new PipelineEventCapture(pipeline);
        using var cts = new CancellationTokenSource();

        var sessionTask = pipeline.HandleSessionAsync(session, cts.Token).AsTask();
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(0).WaitAsync(SignalTimeout);
        await client.SendAudioAsync(VoiceFrame());
        await detector.Analyzed(1).WaitAsync(SignalTimeout);

        // The synthesizer has thrown its own cancellation, and PipelineLoop is awaiting the
        // enumerator's DisposeAsync: the synthesis source is still published, and no filter has run.
        await tts.Unwinding.WaitAsync(SignalTimeout);

        // Act — land the requested ending while the synthesizer is parked, then let it go.
        switch (ending)
        {
            case RequestedEnding.BargIn:
                await client.SendAudioAsync(VoiceFrame());

                // Published on the monitor loop after CancelSynthesis has returned.
                await capture.WaitFor<BargInDetectedEvent>().WaitAsync(SignalTimeout);
                break;

            case RequestedEnding.PipelineDisposal:
                // Cancels the synthesis's own source before it returns.
                await pipeline.DisposeAsync();
                break;

            case RequestedEnding.CallerToken:
                await cts.CancelAsync();
                break;
        }

        tts.Release();
        await client.SendHangupAsync();
        var fault = await Record.ExceptionAsync(() => sessionTask.WaitAsync(SignalTimeout));

        // Assert — after the session ended, so the synthesis activity has already stopped.
        using (new AssertionScope())
        {
            fault.Should().BeNull("a requested ending is not a fault");
            ttsMetrics.Get("tts.syntheses.failed").Should()
                .Be(0, "the requested ending was visible before the pipeline classified the synthesis");
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            activities.Statuses.Should().ContainSingle().Which.Should().NotBe(ActivityStatusCode.Error);
            sessionMetrics.Get("voiceai.sessions.failed").Should().Be(0);
            sessionMetrics.Get("voiceai.sessions.completed").Should().Be(1);

            // Disposal completes the event stream, so for that ending the counter, the log and the
            // activity above carry this assertion.
            capture.Events.OfType<PipelineErrorEvent>().Should().BeEmpty();

            // Today's accounting, not a requirement: a barge-in or a disposal counts the synthesis
            // completed (ADR-0050 E9 debt) and the caller's cancellation counts it as neither.
            ttsMetrics.Get("tts.syntheses.completed").Should().Be(completedToday);
        }

        await CleanupAsync(client, server);
    }

    // ---- Harness ----

    /// <summary>
    /// The exception a synthesizer raises for <paramref name="shape"/>, given the token of a source it
    /// owns. The measured shape carries that token; the plain shape carries none.
    /// </summary>
    private static OperationCanceledException OwnCancellationOf(OwnCancellation shape, CancellationToken ownToken) =>
        shape == OwnCancellation.HttpClientTimeout
            ? new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                new TimeoutException("The operation was canceled."),
                ownToken)
            : new OperationCanceledException("The synthesizer's own deadline elapsed.");

    private static VoiceAiPipeline BuildPipeline(
        SpeechSynthesizer tts, ITurnDetector detector, ILogger<VoiceAiPipeline>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConversationHandler>(
            new FakeConversationHandler().WithResponse("respuesta"));
        services.AddSingleton(detector);
        var provider = services.BuildServiceProvider();

        return new VoiceAiPipeline(
            new FakeSpeechRecognizer().WithTranscript("hola"),
            tts,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VoiceAiPipelineOptions()),
            logger ?? NullLogger<VoiceAiPipeline>.Instance);
    }

    private static async Task<(AudioSocketSession Session, AudioSocketServer Server, AudioSocketClient Client)>
        CreateAudioSessionAsync()
    {
        var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        TaskCompletionSource<AudioSocketSession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += s => { accepted.TrySetResult(s); return ValueTask.CompletedTask; };
        await server.StartAsync(CancellationToken.None);

        // 127.0.0.1, never "localhost": the name resolves ::1 first on this host (ADR-0044).
        var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        return (await accepted.Task.WaitAsync(SignalTimeout), server, client);
    }

    /// <summary>
    /// Hangs up, accepting that the server may already have torn the session down. Pre-fix that is
    /// exactly what happens — the point of the hangup is to end the session in the runs where the
    /// monitor loop is still reading.
    /// </summary>
    private static async Task SendHangupToleratingATornDownSessionAsync(AudioSocketClient client)
    {
        try
        {
            await client.SendHangupAsync();
        }
        catch (IOException)
        {
            // The session was already torn down, so the hangup frame could not be written.
        }
        catch (ObjectDisposedException)
        {
            // The same torn-down session, surfacing as a disposed stream instead of a failed write.
        }
    }

    private static async Task CleanupAsync(AudioSocketClient client, AudioSocketServer server)
    {
        await client.DisposeAsync();
        await server.StopAsync(CancellationToken.None);
    }

    private static ReadOnlyMemory<byte> SilenceFrame() => new byte[320];

    private static ReadOnlyMemory<byte> VoiceFrame()
    {
        var buf = new byte[320];
        for (int i = 0; i < 160; i++)
        {
            const short sample = 5000;
            buf[i * 2] = unchecked((byte)(sample & 0xFF));
            buf[i * 2 + 1] = unchecked((byte)(sample >> 8));
        }
        return buf;
    }
}

/// <summary>
/// Returns one scripted decision per frame and announces which frame it just decided on.
/// </summary>
/// <remarks>
/// <c>VoiceAiPipeline</c> calls <c>Analyze</c> synchronously, once per frame read, on
/// <c>AudioMonitorLoop</c>'s own thread. That makes "send one frame, wait for its signal" an exact
/// ordering primitive: when <see cref="Analyzed"/> completes, the frame has been decided on and the
/// next has not been read. Frames past the end of the script are <see cref="TurnAction.Continue"/>.
/// </remarks>
file sealed class ScriptedTurnDetector : ITurnDetector
{
    private readonly TurnAction[] _script;
    private readonly TaskCompletionSource[] _analyzed;
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _index;

    public ScriptedTurnDetector(params TurnAction[] script)
    {
        _script = script;
        _analyzed = [.. script.Select(
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    }

    /// <summary>Completes once any frame at all has reached the detector.</summary>
    public Task FirstAnalyzed => _first.Task;

    /// <summary>Completes once the frame carrying script step <paramref name="step"/> was decided on.</summary>
    public Task Analyzed(int step) => _analyzed[step].Task;

    public TurnSignal Analyze(ReadOnlySpan<short> samples, bool isAssistantSpeaking)
    {
        _first.TrySetResult();

        var step = _index;
        if (step >= _script.Length)
            return new TurnSignal(TurnAction.Continue);

        _index = step + 1;
        _analyzed[step].TrySetResult();
        return new TurnSignal(_script[step]);
    }

    public void Reset() => _index = 0;
}

/// <summary>
/// Cancels its first synthesis on its own: yields a fixed number of chunks, cancels a source it owns,
/// and raises its own cancellation from the next <c>MoveNextAsync</c> while the token it was handed is
/// still live. Every later synthesis yields one chunk and completes.
/// </summary>
/// <remarks>
/// The pipeline writes each yielded chunk to the session before it asks for the next one, so with one
/// chunk the cancellation arrives after audio has reached the session. The source stands for an elapsed
/// <c>HttpClient.Timeout</c> or a connect deadline: it is linked to nothing the pipeline holds.
/// </remarks>
file sealed class SelfCancellingSpeechSynthesizer : SpeechSynthesizer
{
    private readonly CancellationTokenSource _ownDeadline = new();
    private readonly int _chunksBeforeCancelling;
    private int _syntheses;

    public SelfCancellingSpeechSynthesizer(
        Func<CancellationToken, OperationCanceledException> ownCancellation, int chunksBeforeCancelling)
    {
        OwnCancellation = ownCancellation(_ownDeadline.Token);
        _chunksBeforeCancelling = chunksBeforeCancelling;
    }

    public override string ProviderName => "SelfCancelling";

    /// <summary>The exception the first synthesis ends with.</summary>
    public OperationCanceledException OwnCancellation { get; }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The pipeline runs one synthesis at a time, so a plain count is enough.
        if (++_syntheses > 1)
        {
            yield return new byte[320];
            yield break;
        }

        for (var chunk = 0; chunk < _chunksBeforeCancelling; chunk++)
            yield return new byte[320];

        // Nothing is registered on the source, so this completes synchronously and the throw lands on
        // the MoveNextAsync that follows the last chunk.
        await _ownDeadline.CancelAsync().ConfigureAwait(false);
        throw OwnCancellation;
    }

    public override ValueTask DisposeAsync()
    {
        _ownDeadline.Dispose();
        return base.DisposeAsync();
    }
}

/// <summary>
/// Raises its own cancellation from the first <c>MoveNextAsync</c>, then parks in its enumerator's
/// <c>DisposeAsync</c> until released.
/// </summary>
/// <remarks>
/// <c>await foreach</c> awaits <c>DisposeAsync</c> before the exception it caught reaches the caller's
/// catch filters. Between <see cref="Unwinding"/> and <see cref="Release"/> the synthesis has ended on
/// the synthesizer's own cancellation and has not been classified yet, so whatever the test cancels
/// there is visible when it is.
/// </remarks>
file sealed class UnwindingSelfCancellingSpeechSynthesizer : SpeechSynthesizer
{
    private readonly TaskCompletionSource _unwinding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _ownDeadline = new();

    public UnwindingSelfCancellingSpeechSynthesizer() =>
        OwnCancellation = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled."),
            _ownDeadline.Token);

    public override string ProviderName => "UnwindingSelfCancelling";

    /// <summary>
    /// What an elapsed <c>HttpClient.Timeout</c> raises: inner <see cref="TimeoutException"/> included,
    /// carrying the token of a source this synthesizer owns and cancels when it throws.
    /// </summary>
    public Exception OwnCancellation { get; }

    /// <summary>Completes once the enumerator's <c>DisposeAsync</c> is entered, after the throw.</summary>
    public Task Unwinding => _unwinding.Task;

    /// <summary>Lets the parked <c>DisposeAsync</c> complete. Safe to call more than once.</summary>
    public void Release() => _release.TrySetResult();

    public override IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        CancellationToken ct = default) => new Sequence(this);

    public override ValueTask DisposeAsync()
    {
        Release();
        _ownDeadline.Dispose();
        return base.DisposeAsync();
    }

    private sealed class Sequence(UnwindingSelfCancellingSpeechSynthesizer owner)
        : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) => new Enumerator(owner);
    }

    private sealed class Enumerator(UnwindingSelfCancellingSpeechSynthesizer owner)
        : IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        public ReadOnlyMemory<byte> Current => default;

        public ValueTask<bool> MoveNextAsync()
        {
            owner._ownDeadline.Cancel();
            return ValueTask.FromException<bool>(owner.OwnCancellation);
        }

        public async ValueTask DisposeAsync()
        {
            owner._unwinding.TrySetResult();
            await owner._release.Task.ConfigureAwait(false);
        }
    }
}

/// <summary>One entry the pipeline logged.</summary>
file sealed record LogEntry(LogLevel Level, string Message);

/// <summary>
/// Records what the pipeline logs, so a test can say at which level a synthesis ending was reported.
/// </summary>
file sealed class RecordingLogger : ILogger<VoiceAiPipeline>
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>A snapshot of everything logged so far, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}

/// <summary>Records the status each <c>voiceai.tts.synthesis</c> activity stopped with.</summary>
/// <remarks>
/// An <see cref="ActivityListener"/> is process-wide, like the meters. Only <c>VoiceAiPipeline</c>
/// starts this operation, and every class in this assembly that runs a pipeline shares
/// <see cref="SessionCounterGroup"/>, so what this records belongs to the test that created it.
/// </remarks>
file sealed class SynthesisActivityRecorder : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ActivityStatusCode> _statuses = [];
    private readonly ActivityListener _listener;

    public SynthesisActivityRecorder()
    {
        // Read before registering, never inside ShouldListenTo. Registration calls that delegate, so a
        // first read of the static there initialises the source while the registration is still
        // running. Measured: read that way, the first test in this class to run recorded no synthesis
        // activity at all.
        var voiceAiSource = VoiceAiActivitySource.Source;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, voiceAiSource),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The status of every synthesis activity stopped so far, oldest first.</summary>
    public IReadOnlyList<ActivityStatusCode> Statuses
    {
        get { lock (_gate) return [.. _statuses]; }
    }

    private void OnStopped(Activity activity)
    {
        if (activity.OperationName != "voiceai.tts.synthesis")
            return;

        lock (_gate)
            _statuses.Add(activity.Status);
    }

    public void Dispose() => _listener.Dispose();
}
