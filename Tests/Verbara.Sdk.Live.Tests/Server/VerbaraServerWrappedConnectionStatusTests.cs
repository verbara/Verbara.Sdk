using System.Collections.Concurrent;
using Verbara.Sdk;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Ami.Tests.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A load of live state over a connection that is not an <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> but wraps
/// one, whose <c>Status</c> Asterisk refuses. A wrapper that forwards how the action ended lets the load read the refusal
/// as it does over the real connection: the channel table is left alone and the refusal is logged once at Warning; a
/// <c>Status</c> cut short by the end of the session ends no call and the load throws, as over the real connection. A
/// wrapper that cannot say how the action ended is read as before: its empty answer reconciles the table. A test double
/// configured only on the plain overload keeps receiving its configured <c>Status</c>.
/// </summary>
/// <remarks>
/// The forwarding and the plain wrapper each wrap a real connection over the in-memory harness, served by a
/// <see cref="BootingAsterisk"/>; the server is built over the wrapper, so it reads the connection only through the
/// interface. The two channels are the two legs of one call. Every wait is bounded by <see cref="Run.Bound"/>.
/// </remarks>
public sealed class VerbaraServerWrappedConnectionStatusTests
{
    private const string StatusRefused = "[LIVE] Status refused";

    private static IReadOnlyList<StatusChannel> OneCallTwoLegs() =>
    [
        new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
        new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
    ];

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepEveryHeldChannelAndWarnOnce_WhenAForwardingWrappersStatusIsRefused()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        var log = new SignalingLogger<VerbaraServer>();
        await using var server = new VerbaraServer(new ForwardingAmiConnectionWrapper(run.Connection), log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        var heldAtStart = server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.StatusRefusedFromAsk = 2;
        await server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        var refusals = log.Entries.Where(e => e.Line.StartsWith(StatusRefused, StringComparison.Ordinal)).ToList();

        using (new AssertionScope())
        {
            heldAtStart.Should().Be(2, "the start loaded both legs of the call");
            peer.Asked("Status").Should().Be(2, "the start's Status and the refused one");
            removed.Should().BeEmpty("a refusal reported through a forwarding wrapper is no evidence that a channel is gone");
            server.Channels.ChannelCount.Should().Be(2, "both legs Asterisk still holds are still held");
            refusals.Should().ContainSingle("the load says once that Asterisk refused its Status")
                .Which.Level.Should().Be(LogLevel.Warning);
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldEndNoCall_WhenTheSessionEndsWhileAForwardingWrappersStatusIsRead()
    {
        // Two calls, one channel each, held from the start. The next snapshot lists the first channel and the session
        // ends before StatusComplete: through a wrapper that forwards how the action ended, the second channel's absence
        // from an unfinished snapshot proves nothing, as over the real connection.
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels =
            [
                new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
                new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.2"),
            ],
        };
        await using var run = await Run.ConnectAsync(peer);
        var log = new SignalingLogger<VerbaraServer>();
        await using var server = new VerbaraServer(new ForwardingAmiConnectionWrapper(run.Connection), log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        var heldAtStart = server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.Close = PeerClose.DuringStatus;
        peer.StatusChannelsBeforeClose = 1;
        var outcome = await Record.ExceptionAsync(() => server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            removed.Should().BeEmpty("a snapshot the session ended before it completed ends no call, through a forwarding wrapper too");
            server.Channels.ChannelCount.Should().Be(2, "both channels are still held");
            server.Channels.GetByUniqueId("1700000000.1").Should().NotBeNull("the listed channel is still held");
            server.Channels.GetByUniqueId("1700000000.2").Should().NotBeNull(
                "the channel the unfinished snapshot never reached is still held");
            outcome.Should().BeOfType<AmiNotConnectedException>(
                "a direct call to the load throws when its session ends, and returns no partial state");
            heldAtStart.Should().Be(2, "the start loaded both channels");
            peer.Asked("Status").Should().Be(2, "the start's snapshot and the reload's");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldReadTheRefusalAsBefore_WhenAWrapperCannotSayHowTheStatusEnded()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        var log = new SignalingLogger<VerbaraServer>();
        await using var server = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection), log) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        var heldAtStart = server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.StatusRefusedFromAsk = 2;
        await server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            heldAtStart.Should().Be(2, "the start loaded both legs of the call");
            peer.Asked("Status").Should().Be(2, "the start's Status and the refused one");
            removed.Should().BeEquivalentTo(["1700000000.1", "1700000000.2"],
                "an outcome the wrapper cannot report is read as before: the refusal's empty answer reconciles the table");
            log.Entries.Should().NotContain(e => e.Line.StartsWith(StatusRefused, StringComparison.Ordinal),
                "nothing told the load that Asterisk refused");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepTheListedChannel_WhenASubstituteIsConfiguredOnlyOnThePlainOverload()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Connected);
        connection.AsteriskVersion.Returns("22.0.0");
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(call => Reply(call.ArgAt<ManagerAction>(0)));
        await using var server = new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>());

        await server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        server.Channels.ChannelCount.Should().Be(1,
            "a test double configured only on the plain overload still answers the load's Status with the channel it lists");
    }

    private static async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();
        if (action is not StatusAction)
            yield break;

        yield return new StatusEvent
        {
            UniqueId = "1700000000.7",
            Channel = "PJSIP/trunk-0007",
            RawFields = new Dictionary<string, string>(StringComparer.Ordinal) { ["ChannelState"] = "6" },
        };
    }
}
