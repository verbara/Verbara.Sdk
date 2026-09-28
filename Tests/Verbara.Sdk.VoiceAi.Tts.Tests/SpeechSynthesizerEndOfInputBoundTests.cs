using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// The four WebSocket synthesizers after their end of input: a vendor that then says nothing for 10 s,
/// with the connection still open, ends the stream as a <see cref="SpeechProviderFailureSignal.Transport"/>
/// failure, and a vendor that keeps sending is never cut.
/// </summary>
/// <remarks>
/// <para>
/// Each synthesizer ends its input in band (Cartesia's request, Deepgram's <c>Flush</c>, ElevenLabs'
/// empty text chunk, LMNT's <c>eof</c>) and then reads until the vendor ends the session. Only the
/// caller's token bounded that read, so a vendor that went quiet held the caller, and the pipeline above
/// it, for as long as the call lasted.
/// </para>
/// <para>
/// The 10 s run on the synthesizer's clock, a <see cref="FakeTimeProvider"/> here, and the peer acts
/// only when the test asks (<see cref="EndOfInputPeer"/>). The first test waits for the bound to arm
/// (<see cref="FakeTimeProvider.TimersArmed"/>) before it moves the clock, because the peer can read the
/// end of input before the synthesizer's send of it has returned. The others do not wait for it, so they
/// run where no bound exists: the red of the breaking half is then the late frame completing the
/// stream. They move the clock once the audio sent on the end of input has reached the caller, a full
/// round trip after the synthesizer armed the bound on its send side. Nothing waits on the wall clock
/// except <see cref="SignalTimeout"/>, and reaching it is a failure.
/// </para>
/// </remarks>
public sealed class SpeechSynthesizerEndOfInputBoundTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The ruled silence bound after the end of input.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Clients => SynthesizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenTheVendorIsSilentForTheBoundAfterTheEndOfInput(string client)
    {
        // Arrange — the vendor answers the end of input with one chunk of audio, and then with nothing.
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        var synthesizer = SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock);
        var caller = new SynthesisCaller(synthesizer, CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act
        await WaitForTheBoundToArmAsync(clock);
        clock.Advance(Bound);
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        var failure = ending.Should().BeOfType<SpeechProviderFailureException>(
            "a vendor silent for the bound after the end of input left the audio incomplete").Subject;
        using (new AssertionScope())
        {
            failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
            failure.Provider.Should().Be(synthesizer.ProviderName);
            failure.InnerException.Should().BeOfType<TimeoutException>()
                .Which.Message.Should().Contain(synthesizer.ProviderName, "the timeout names the provider that went silent");
            caller.Chunks.Should().Be(1, "the audio sent before the silence was delivered");
        }
    }

    /// <summary>
    /// Every vendor frame restarts the bound, so a vendor that keeps sending is never cut, however long
    /// it takes in total: eight frames 2 s apart end 16 s after the end of input with nine chunks. True
    /// before the bound existed as well, so it is a control there; it goes red when a frame no longer
    /// restarts the bound (the vendor is then cut 10 s after the end of input, with 5 of the 9 chunks).
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldComplete_WhenTheVendorKeepsSendingPastTheBound(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var clock = new FakeTimeProvider();
        var caller = new SynthesisCaller(
            SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock), CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act — a chunk reaching the caller means the synthesizer read its frame, so the bound restarted
        // before the clock moves again.
        var driving = await Record.ExceptionAsync(async () =>
        {
            for (var frame = 0; frame < 8; frame++)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                await peer.SendFrameAsync().WaitAsync(SignalTimeout);
                await caller.NextChunkAsync().WaitAsync(SignalTimeout);
            }
        });
        await AnswerAsync(peer);
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeNull("a vendor that keeps sending is not silent, however long it takes in total");
            driving.Should().BeNull("every frame the vendor sent reached the caller");
            caller.Chunks.Should().Be(9, "the audio sent on the end of input and one chunk per frame");
        }
    }

    /// <summary>
    /// The breaking half: a vendor whose only answer to the end of input comes 12 s after it, a single
    /// gap longer than the bound, now fails the synthesis at 10 s. Before the bound the late frame
    /// completed the stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenTheOnlyAnswerComesAfterTheBound(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var clock = new FakeTimeProvider();
        var caller = new SynthesisCaller(
            SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock), CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act — 10 s with no frame, then the vendor's one late frame and its end, 2 s later. The late
        // answer's own outcome is not asserted: once the bound has ended the session its socket is gone,
        // so the frame may or may not still be written.
        clock.Advance(Bound);
        _ = await Record.ExceptionAsync(async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await peer.SendFrameAsync().WaitAsync(SignalTimeout);
            await peer.AnswerAsync().WaitAsync(SignalTimeout);
        });
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeOfType<SpeechProviderFailureException>(
                "a single gap longer than the bound after the end of input fails the synthesis");
            caller.Chunks.Should().Be(1, "the late frame came after the bound had ended the session");
        }

        var failure = (SpeechProviderFailureException)ending!;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.InnerException.Should().BeOfType<TimeoutException>();
    }

    /// <summary>
    /// The breaking half's twin, green before the bound existed and after: the only answer comes 9.9 s
    /// after the end of input, inside the bound, and completes the synthesis.
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldComplete_WhenTheOnlyAnswerComesJustInsideTheBound(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var clock = new FakeTimeProvider();
        var caller = new SynthesisCaller(
            SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock), CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act
        var driving = await Record.ExceptionAsync(async () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(9_900));
            await peer.SendFrameAsync().WaitAsync(SignalTimeout);
            await caller.NextChunkAsync().WaitAsync(SignalTimeout);
        });
        await AnswerAsync(peer);
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeNull("the answer came inside the bound");
            driving.Should().BeNull("the vendor's frame reached the caller");
            caller.Chunks.Should().Be(2);
        }
    }

    /// <summary>
    /// A control, green before the bound existed and after: the caller's own cancellation while the
    /// synthesizer waits on its vendor is a cancellation, never a provider failure (<c>ADR-0050</c> E6).
    /// </summary>
    /// <remarks>
    /// It does not wait for the bound to arm, because nothing arms before the fix and a control has to
    /// run there. The cancel lands once the vendor's answer to the end of input has reached the caller.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTheCallerCancelsWhileTheBoundRuns(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var caller = new SynthesisCaller(
            SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock), cts.Token);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act
        await cts.CancelAsync();
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeAssignableTo<OperationCanceledException>("the caller asked for the ending");
            ending.Should().NotBeOfType<SpeechProviderFailureException>();
            caller.Chunks.Should().Be(1);
        }
    }

    /// <summary>
    /// A control, green before the bound existed and after: a barge-in reaches the synthesizer as the
    /// caller's token, the session's linked with the barge-in's as <c>VoiceAiPipeline</c> links them.
    /// Landing 9 s into the bound, it ends the synthesis as a cancellation, not as a provider failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenABargeInCancelsItWhileTheBoundRuns(string client)
    {
        // Arrange
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        using var session = new CancellationTokenSource();
        using var bargeIn = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, bargeIn.Token);
        var caller = new SynthesisCaller(
            SynthesizerEndOfInputPeers.CreateSynthesizer(client, peer.Port, clock), linked.Token);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextChunkAsync().WaitAsync(SignalTimeout);

        // Act — 9 s of the bound pass, and the caller speaks over the answer.
        clock.Advance(TimeSpan.FromSeconds(9));
        await bargeIn.CancelAsync();
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeAssignableTo<OperationCanceledException>("the barge-in asked for the ending");
            ending.Should().NotBeOfType<SpeechProviderFailureException>();
            session.IsCancellationRequested.Should().BeFalse("only the barge-in ended the synthesis");
            caller.Chunks.Should().Be(1);
        }
    }

    /// <summary>
    /// Has the vendor end the synthesis: its terminal frame, then its close. What the stream then does
    /// is asserted on the stream, not here.
    /// </summary>
    /// <remarks>
    /// The request's own outcome is not asserted. Three of the four synthesizers end on the vendor's
    /// terminal frame (Cartesia <c>done</c>, Deepgram <c>Flushed</c>, LMNT <c>finish</c>) and drop the
    /// connection at once, which can beat the peer's close frame: the request then faults with the
    /// session's ending although everything the synthesizer needed arrived (seen on the Cartesia rows).
    /// A terminal frame that never arrived shows as the stream not completing.
    /// </remarks>
    private static async Task AnswerAsync(EndOfInputPeer peer)
        => _ = await Record.ExceptionAsync(() => peer.AnswerAsync().WaitAsync(SignalTimeout));

    /// <summary>
    /// Waits until the synthesizer has armed a timer on <paramref name="clock"/>: its end of input is
    /// out and the bound is running. Before the bound existed nothing arms, and this times out.
    /// </summary>
    private static async Task WaitForTheBoundToArmAsync(FakeTimeProvider clock)
    {
        var due = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
        due.Should().Be(Bound, "the bound armed is the silence bound, not some other timer");
    }

    /// <summary>
    /// The caller's side of one synthesis: it enumerates on its own task and hands each chunk to the
    /// test as it arrives, so a test can wait for a chunk instead of for time.
    /// </summary>
    private sealed class SynthesisCaller
    {
        private readonly Channel<int> _arrived = Channel.CreateUnbounded<int>();
        private int _chunks;

        public SynthesisCaller(SpeechSynthesizer synthesizer, CancellationToken ct)
        {
            Run = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in synthesizer.SynthesizeAsync("hello world", AudioFormat.Slin16Mono8kHz, ct))
                    {
                        Interlocked.Increment(ref _chunks);
                        _arrived.Writer.TryWrite(chunk.Length);
                    }
                }
                finally
                {
                    _arrived.Writer.TryComplete();
                }
            }, CancellationToken.None); // the token ends the enumeration, which reports it; it must not skip the task
        }

        /// <summary>The enumeration: completes when the stream ends, faulted with what it threw.</summary>
        public Task Run { get; }

        /// <summary>How many chunks have been delivered so far.</summary>
        public int Chunks => Volatile.Read(ref _chunks);

        /// <summary>Completes with the length of the next chunk delivered to the caller.</summary>
        public Task<int> NextChunkAsync() => _arrived.Reader.ReadAsync().AsTask();
    }
}
