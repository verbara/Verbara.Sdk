using System.Net.WebSockets;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// The connect of the four WebSocket synthesizers against a far end that takes the connection and does
/// not answer the upgrade, the synthesis twin of the recognizers' suite. The client's own bound ends
/// the connect as a failed upgrade, the <see cref="SpeechProviderFailureSignal.Handshake"/> failure a
/// refused upgrade already takes (<c>ADR-0050</c> E7); the caller's cancellation still ends it as a
/// cancellation (<c>ADR-0050</c> E6); and an upgrade answered inside the bound starts the synthesis.
/// </summary>
/// <remarks>
/// <para>
/// Before the bound, Cartesia, Deepgram and LMNT ended an unanswered upgrade with the
/// <see cref="TaskCanceledException"/> their own caller's cancellation also produces, although the
/// caller had cancelled nothing, and ElevenLabs had no bound at all: it waited for as long as the
/// caller's token allowed.
/// </para>
/// <para>
/// The bound runs on the synthesizer's clock, a <see cref="FakeTimeProvider"/> here. It is armed before
/// the upgrade request is written, so once <see cref="StalledUpgradeListener.RequestReceived"/> has
/// fired, a move of the clock reaches it. The listener answers only when the test asks
/// (<see cref="StalledUpgradeListener.AnswerUpgradeAsync"/>). Nothing waits on the wall clock except
/// <see cref="SignalTimeout"/>, and reaching it is a failure.
/// </para>
/// </remarks>
public sealed class SpeechSynthesizerConnectBoundTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Clients => SynthesizerEndOfInputPeers.Clients;

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailure_WhenTheUpgradeIsNeverAnswered(string client)
    {
        // Arrange — a one-second bound, so that where the bound still ran on the wall clock it fired
        // inside the test's wait instead of past it.
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var clock = new FakeTimeProvider();
        var synthesizer = SynthesizerEndOfInputPeers.CreateSynthesizer(client, stalled.Port, clock, connectTimeoutSeconds: 1);
        var run = SynthesizeAsync(synthesizer, CancellationToken.None);
        await stalled.RequestReceived.WaitAsync(SignalTimeout);

        // Act
        clock.Advance(TimeSpan.FromSeconds(1));
        var fault = await Record.ExceptionAsync(() => run.WaitAsync(SignalTimeout));

        // Assert
        var failure = fault.Should().BeOfType<SpeechProviderFailureException>(
            "an upgrade the far end never answered is a failed connect, not a cancellation the caller asked for").Subject;
        using (new AssertionScope())
        {
            failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
            failure.Code.Should().BeNull("the far end never answered, so there is no HTTP status to report");
            failure.Provider.Should().Be(synthesizer.ProviderName);
            failure.InnerException.Should().BeOfType<WebSocketException>()
                .Which.InnerException.Should().BeOfType<TimeoutException>()
                .Which.Message.Should().Contain("1 s", "the bound is the client's ConnectTimeoutSeconds");
            stalled.RequestCount.Should().Be(1);
        }
    }

    /// <summary>
    /// An upgrade answered 4 s after the request, inside the default 5 s bound, starts the synthesis,
    /// and the bound ends with the connect: the session then runs past the moment it would have fired.
    /// True before the bound moved to the synthesizer's clock as well.
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldStartTheSynthesis_WhenTheUpgradeIsAnsweredInsideTheBound(string client)
    {
        // Arrange — the answered connection is served by the client's own end-of-input peer.
        await using var peer = SynthesizerEndOfInputPeers.Create(client, EndOfInputPeerMode.AnswerOnRequest);
        await using var stalled = new StalledUpgradeListener(peer.HandleSessionAsync);
        stalled.Start();
        var clock = new FakeTimeProvider();
        var run = SynthesizeAsync(SynthesizerEndOfInputPeers.CreateSynthesizer(client, stalled.Port, clock), CancellationToken.None);
        await stalled.RequestReceived.WaitAsync(SignalTimeout);

        // Act — the upgrade is answered 4 s after the request; the session then runs to 6 s, past the
        // 5 s bound, before the vendor ends the synthesis.
        var driving = await Record.ExceptionAsync(async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            await stalled.AnswerUpgradeAsync().WaitAsync(SignalTimeout);
            await peer.EndOfInputSeen.WaitAsync(SignalTimeout);
            clock.Advance(TimeSpan.FromSeconds(2));
        });
        await AnswerAsync(peer);
        int? chunks = null;
        var ending = await Record.ExceptionAsync(async () => chunks = await run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            ending.Should().BeNull("a slow upgrade inside the bound still connects, and the synthesis proceeds normally");
            driving.Should().BeNull("the upgrade was answered and the session reached its end of input");
            chunks.Should().Be(1, "the vendor answered the end of input with one chunk of audio, then ended the synthesis");
        }
    }

    /// <summary>
    /// A control, green before the bound moved and after: the caller withdrawing while the upgrade is
    /// held is a cancellation, never a provider failure (<c>ADR-0050</c> E6).
    /// </summary>
    [Theory]
    [MemberData(nameof(Clients))]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTheCallerCancelsDuringTheUpgrade(string client)
    {
        // Arrange
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        // The token goes to the subject and nowhere else (ADR-0052 F3).
        var run = SynthesizeAsync(SynthesizerEndOfInputPeers.CreateSynthesizer(client, stalled.Port, clock), cts.Token);
        await stalled.RequestReceived.WaitAsync(SignalTimeout);

        // Act — the connect is demonstrably in flight, and the bound has not run.
        await cts.CancelAsync();
        var fault = await Record.ExceptionAsync(() => run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeAssignableTo<OperationCanceledException>(
                "a cancellation the caller asked for is never reported as a provider failure");
            stalled.RequestCount.Should().Be(1);
        }
    }

    /// <summary>
    /// ElevenLabs had no connect bound before this one. Built with default options it now has the 5 s
    /// every other WebSocket speech client ships, armed as the dial starts.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailureAfterFiveSeconds_WhenElevenLabsHasDefaultOptions()
    {
        // Arrange
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var clock = new FakeTimeProvider();
        var synthesizer = SynthesizerEndOfInputPeers.CreateSynthesizer("ElevenLabs", stalled.Port, clock);
        var run = SynthesizeAsync(synthesizer, CancellationToken.None);
        await stalled.RequestReceived.WaitAsync(SignalTimeout);

        // Act — the bound armed is read off the clock, then run out.
        var due = await clock.TimersArmed.ReadAsync().AsTask().WaitAsync(SignalTimeout);
        clock.Advance(due);
        var fault = await Record.ExceptionAsync(() => run.WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            new ElevenLabsOptions().ConnectTimeoutSeconds.Should().Be(5);
            due.Should().Be(TimeSpan.FromSeconds(5), "the default bound is the option's default");
            fault.Should().BeOfType<SpeechProviderFailureException>()
                .Which.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
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
    /// session's ending although everything the synthesizer needed arrived. A terminal frame that never
    /// arrived shows as the stream not completing.
    /// </remarks>
    private static async Task AnswerAsync(EndOfInputPeer peer)
        => _ = await Record.ExceptionAsync(() => peer.AnswerAsync().WaitAsync(SignalTimeout));

    /// <summary>
    /// The caller's side of one synthesis, on its own task: how many chunks were delivered once the
    /// stream completes, or what it threw.
    /// </summary>
    private static Task<int> SynthesizeAsync(SpeechSynthesizer synthesizer, CancellationToken ct)
        => Task.Run(async () =>
        {
            var chunks = 0;
            await foreach (var _ in synthesizer.SynthesizeAsync("hello world", AudioFormat.Slin16Mono8kHz, ct))
                chunks++;

            return chunks;
        }, CancellationToken.None); // the token ends the enumeration, which reports it; it must not skip the task
}
