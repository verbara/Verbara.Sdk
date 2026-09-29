using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// The four WebSocket recognizers after their end of input: a vendor that then says nothing for 10 s,
/// with the connection still open, ends the stream as a <see cref="SpeechProviderFailureSignal.Transport"/>
/// failure, and nothing else does.
/// </summary>
/// <remarks>
/// <para>
/// Each recognizer ends its input in band (Deepgram <c>CloseStream</c>, AssemblyAI <c>Terminate</c>,
/// Cartesia <c>done</c>, Speechmatics <c>EndOfStream</c>) and then reads until the vendor ends the
/// session. Only the caller's token bounded that read, so a vendor that went quiet held the caller,
/// and the pipeline above it, for as long as the call lasted.
/// </para>
/// <para>
/// The 10 s run on the recognizer's clock, a <see cref="FakeTimeProvider"/> here, and the peer acts
/// only when the test asks (<see cref="EndOfInputPeer"/>). The test that needs the bound running
/// waits for it to arm (<see cref="FakeTimeProvider.TimersArmed"/>) before it moves the clock, because
/// the peer can read the end of input before the recognizer's send of it has returned. The tests of
/// what the bound must leave alone do not wait for it, so they run, and pass, where no bound exists.
/// Nothing waits on the wall clock except <see cref="SignalTimeout"/>, and reaching it is a failure.
/// </para>
/// </remarks>
public sealed class SpeechRecognizerEndOfInputBoundTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The ruled silence bound after the end of input.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Clients => RecognizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task StreamAsync_ShouldThrowTransportFailure_WhenTheVendorIsSilentForTheBoundAfterTheEndOfInput(string client)
    {
        // Arrange — the vendor transcribes the audio, and then never answers the end of input.
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        var caller = new StreamingCaller(
            RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port, clock),
            RecognizerEndOfInputPeers.Frames(25),
            CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextTranscriptAsync().WaitAsync(SignalTimeout);

        // Act
        await WaitForTheBoundToArmAsync(clock);
        clock.Advance(Bound);
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        var failure = ending.Should().BeOfType<SpeechProviderFailureException>(
            "a vendor silent for the bound after the end of input left the result incomplete").Subject;
        using (new AssertionScope())
        {
            failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
            failure.Provider.Should().Be(client);
            failure.InnerException.Should().BeOfType<TimeoutException>()
                .Which.Message.Should().Contain(client, "the timeout names the provider that went silent");
            caller.Transcripts.Should().Equal(["hello"], "the item sent before the end of input was delivered");
        }
    }

    /// <summary>
    /// Every vendor frame restarts the bound, so a vendor that keeps sending is never cut, however long
    /// it takes in total. True before the bound existed as well: this pins the restart, and goes red
    /// when a frame no longer restarts it (the vendor is then cut at 10 s).
    /// </summary>
    /// <remarks>
    /// It does not wait for the bound to arm, so that it runs where nothing arms. Wherever the arm lands
    /// among the rounds, no 6 s step reaches 10 s from the last restart: each round's frame reaches the
    /// caller, and so restarts the bound, before the clock moves again.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task StreamAsync_ShouldComplete_WhenTheVendorKeepsSendingPastTheBound(string client)
    {
        // Arrange — three frames 6 s apart put the answer 18 s after the end of input, past the 10 s
        // bound in total but never 10 s from the last frame.
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.FrameOnRequest);
        peer.Start();
        var clock = new FakeTimeProvider();
        var caller = new StreamingCaller(
            RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port, clock),
            RecognizerEndOfInputPeers.Frames(25),
            CancellationToken.None);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextTranscriptAsync().WaitAsync(SignalTimeout);

        // Act — a frame reaching the caller means the recognizer read it, so the bound restarted
        // before the clock moves again.
        var driving = await Record.ExceptionAsync(async () =>
        {
            for (var round = 0; round < 3; round++)
            {
                clock.Advance(TimeSpan.FromSeconds(6));
                await peer.SendFrameAsync().WaitAsync(SignalTimeout);
                await caller.NextTranscriptAsync().WaitAsync(SignalTimeout);
            }

            await peer.AnswerAsync().WaitAsync(SignalTimeout);
        });
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeNull("a vendor that keeps sending is not silent, however long it takes in total");
            driving.Should().BeNull("every frame the vendor sent reached the caller");
            caller.Transcripts.Should().Equal("hello", "hello", "hello", "hello", "hello world");
        }
    }

    /// <summary>
    /// The bound starts at the end of input: a caller still sending audio to a vendor that has said
    /// nothing for 30 s is not failed. True before the bound existed as well; it goes red when the
    /// bound arms at the session's start instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task StreamAsync_ShouldNotFail_WhenTheVendorIsSilentBeforeTheEndOfInput(string client)
    {
        // Arrange — the caller is still talking: the audio holds after five frames until the test
        // ends it.
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.AnswerOnRequest);
        peer.Start();
        var clock = new FakeTimeProvider();
        var audio = new HeldAudio(framesBeforeTheHold: 5);
        var caller = new StreamingCaller(
            RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port, clock),
            audio.Frames(),
            CancellationToken.None);

        await audio.Holding.WaitAsync(SignalTimeout);
        await caller.NextTranscriptAsync().WaitAsync(SignalTimeout);

        // Act — 30 s pass on the recognizer's clock with the input still open and the vendor quiet.
        var driving = await Record.ExceptionAsync(async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            audio.EndInput();
            await Task.WhenAny(peer.EndOfInputSeen, caller.Run).WaitAsync(SignalTimeout);
            await peer.AnswerAsync().WaitAsync(SignalTimeout);
        });
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeNull("the bound starts at the end of input, not while the caller is still sending");
            driving.Should().BeNull("the input ended and the vendor answered it");
            caller.Transcripts.Should().Equal("hello", "hello world");
        }
    }

    /// <summary>
    /// A control, green before the bound existed and after: the caller's own cancellation while the
    /// recognizer waits on its vendor is a cancellation, never a provider failure (<c>ADR-0050</c> E6).
    /// </summary>
    /// <remarks>
    /// It does not wait for the bound to arm, because nothing arms before the fix and a control has to
    /// run there. The cancel lands once the end of input has reached the vendor.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task StreamAsync_ShouldThrowOperationCanceled_WhenTheCallerCancelsWhileTheBoundRuns(string client)
    {
        // Arrange
        await using var peer = RecognizerEndOfInputPeers.Create(client, EndOfInputPeerMode.NeverAnswer);
        peer.Start();
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var caller = new StreamingCaller(
            RecognizerEndOfInputPeers.CreateRecognizer(client, peer.Port, clock),
            RecognizerEndOfInputPeers.Frames(25),
            cts.Token);

        await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
        await caller.NextTranscriptAsync().WaitAsync(SignalTimeout);

        // Act
        await cts.CancelAsync();
        var ending = await Record.ExceptionAsync(() => caller.Run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeAssignableTo<OperationCanceledException>("the caller asked for the ending");
            ending.Should().NotBeOfType<SpeechProviderFailureException>();
            caller.Transcripts.Should().Equal(["hello"]);
        }
    }

    /// <summary>
    /// Waits until the recognizer has armed its silence bound on <paramref name="clock"/>: its end of
    /// input is out and the bound is running. The connect bound arms on the same clock first, as the
    /// dial starts, with the option's 5 s, and is released once the session opens; only an arm of
    /// <see cref="Bound"/> counts. Before the bound existed nothing arms it, and this times out.
    /// </summary>
    private static async Task WaitForTheBoundToArmAsync(FakeTimeProvider clock)
    {
        TimeSpan due;
        do
        {
            due = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
        }
        while (due != Bound);
    }

    /// <summary>
    /// The caller's side of one stream: it enumerates on its own task and hands each transcript to
    /// the test as it arrives, so a test can wait for an item instead of for time.
    /// </summary>
    private sealed class StreamingCaller
    {
        private readonly Channel<string> _arrived = Channel.CreateUnbounded<string>();
        private readonly List<string> _transcripts = [];
        private readonly Lock _gate = new();

        public StreamingCaller(
            SpeechRecognizer recognizer, IAsyncEnumerable<ReadOnlyMemory<byte>> audio, CancellationToken ct)
        {
            Run = Task.Run(async () =>
            {
                try
                {
                    await foreach (var result in recognizer.StreamAsync(audio, AudioFormat.Slin16Mono8kHz, ct))
                    {
                        lock (_gate)
                            _transcripts.Add(result.Transcript);
                        _arrived.Writer.TryWrite(result.Transcript);
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

        /// <summary>Every transcript delivered so far, in order.</summary>
        public IReadOnlyList<string> Transcripts
        {
            get
            {
                lock (_gate)
                    return [.. _transcripts];
            }
        }

        /// <summary>Completes with the next transcript delivered to the caller.</summary>
        public Task<string> NextTranscriptAsync() => _arrived.Reader.ReadAsync().AsTask();
    }

    /// <summary>
    /// Audio the test ends: a few frames, then a hold until <see cref="EndInput"/>, then one more frame
    /// and the end of input.
    /// </summary>
    private sealed class HeldAudio(int framesBeforeTheHold)
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes when the recognizer has taken every frame before the hold and asked for the next:
        /// its session is open and its send loop is running, before any end of input.
        /// </summary>
        public Task Holding => _holding.Task;

        /// <summary>Lets the audio finish, so the recognizer sends its end of input.</summary>
        public void EndInput() => _released.TrySetResult();

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> Frames([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < framesBeforeTheHold; i++)
                yield return new byte[320];

            _holding.TrySetResult();
            await _released.Task.WaitAsync(ct);
            yield return new byte[320];
        }
    }
}
