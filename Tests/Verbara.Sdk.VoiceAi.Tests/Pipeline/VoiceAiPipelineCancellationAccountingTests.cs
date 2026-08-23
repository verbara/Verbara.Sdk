using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Pins how a <see cref="VoiceAiPipeline"/> session is classified when it ends for a reason nobody
/// called a fault: the caller cancelled the token, or the caller disposed the pipeline while the
/// assistant was still speaking.
/// </summary>
/// <remarks>
/// <para>
/// Both endings used to arrive at the bare <c>catch</c> that wraps the two loops, which counted a
/// failure and rethrew. <c>ADR-0054</c> settles the disagreement this created with
/// <c>OpenAiRealtimeBridge</c> (<c>ADR-0053</c>): one <c>ISessionHandler</c> interface reports one
/// number, and a requested ending is a completion.
/// </para>
/// <para>
/// Nothing here waits on a clock to establish an ordering. The synthesizer parks between chunks and
/// says so, and the turn detector — which the pipeline calls synchronously, one call per frame —
/// says which frame it just decided on. Every step is therefore ordered by construction, which is
/// the whole point: a race test built on a delay would reintroduce the defect class
/// <c>ADR-0045</c> and <c>ADR-0053</c> exist about.
/// </para>
/// </remarks>
[Collection(SessionCounterGroup.Name)]
public sealed class VoiceAiPipelineCancellationAccountingTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string MeterName = "Verbara.Sdk.VoiceAi";

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

    // ---- Harness ----

    private static VoiceAiPipeline BuildPipeline(SpeechSynthesizer tts, ITurnDetector detector)
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
            NullLogger<VoiceAiPipeline>.Instance);
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
        }
        catch (ObjectDisposedException)
        {
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
/// Yields one chunk, then parks until released or cancelled.
/// </summary>
/// <remarks>
/// Parking after the first chunk is what makes "a synthesis is in flight" a fact rather than a
/// probability: <c>_ttsCts</c> is assigned before the enumeration starts and released in the
/// <c>finally</c> that only runs once the enumeration ends, so anything the test does between
/// <see cref="Parked"/> and <see cref="Release"/> happens inside that window.
/// </remarks>
file sealed class ParkingSpeechSynthesizer : SpeechSynthesizer
{
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override string ProviderName => "Parking";

    /// <summary>Completes once the first chunk has been consumed and the synthesis is parked.</summary>
    public Task Parked => _parked.Task;

    /// <summary>Ends the park. Safe to call when the synthesis was already cancelled.</summary>
    public void Release() => _release.TrySetResult();

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new byte[320];
        _parked.TrySetResult();
        await _release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        Release();
        return base.DisposeAsync();
    }
}
