using System.Collections.Concurrent;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The server's reads of its connection's loss mark for the session engine, and the mark a reconciliation carries on the
/// channels it admits. Over the SDK's <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> both reads carry the
/// connection's identity, its session and its dropped count, at the position delivery has started
/// (<see cref="VerbaraServer.ReadSessionLossMark"/>) or applied (<see cref="VerbaraServer.ReadAppliedSessionLossMark"/>);
/// over any other connection both are <see langword="null"/>, never a mark that reads as complete. A channel a
/// reconciliation admits carries the mark sampled when the read window opened, before <c>Status</c> was sent; a live
/// arrival carries none, and an admission from a snapshot over another connection keeps its snapshot provenance with no
/// mark.
/// </summary>
/// <remarks>
/// The tests over a real connection run it through <see cref="Run"/> over a <see cref="BootingAsterisk"/> that has
/// finished starting; every wait is bounded by <see cref="Run.Bound"/>.
/// </remarks>
public sealed class VerbaraServerSessionLossMarkTests
{
    private static IReadOnlyList<StatusChannel> OneCallTwoLegs() =>
    [
        new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
        new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
    ];

    [Fact]
    public async Task ReadSessionLossMark_ShouldBeNull_WhenTheConnectionIsNotAmiConnection()
    {
        await using var server = new VerbaraServer(Substitute.For<IAmiConnection>(), NullLogger<VerbaraServer>.Instance);

        var mark = server.ReadSessionLossMark();

        mark.Should().BeNull("a connection that is not the SDK's reports no drops and no ordered session");
    }

    [Fact]
    public async Task ReadAppliedSessionLossMark_ShouldBeNull_WhenTheConnectionIsNotAmiConnection()
    {
        await using var server = new VerbaraServer(Substitute.For<IAmiConnection>(), NullLogger<VerbaraServer>.Instance);

        var mark = server.ReadAppliedSessionLossMark();

        mark.Should().BeNull("a connection that is not the SDK's reports no drops and no ordered session");
    }

    [Fact]
    public async Task ReadSessionLossMark_ShouldCarryTheConnectionIdentity_WhenOverAmiConnection()
    {
        var firstPeer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        var secondPeer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var first = await Run.StartAsync(firstPeer);
        await using var second = await Run.StartAsync(secondPeer);

        var delivered = first.Server.ReadSessionLossMark();
        var applied = first.Server.ReadAppliedSessionLossMark();
        var other = second.Server.ReadSessionLossMark();

        using (new AssertionScope())
        {
            delivered.Should().NotBeNull("the SDK's connection reports its mark");
            applied.Should().NotBeNull("the SDK's connection reports its applied mark");
            delivered!.Value.ConnectionId.Should().NotBe(Guid.Empty, "the identity was drawn when the connection was built");
            applied!.Value.ConnectionId.Should().Be(delivered.Value.ConnectionId, "both reads name the same connection");
            delivered.Value.Should().Be(new SessionLossMark(delivered.Value.ConnectionId, 1, 0),
                "the first AMI session, which has dropped nothing");
            applied.Value.Should().Be(delivered.Value, "every observer of the last delivered event has returned");
            other!.Value.ConnectionId.Should().NotBe(delivered.Value.ConnectionId,
                "another connection instance carries another identity, so its first session is not equal to this one's");
            other.Value.Session.Should().Be(delivered.Value.Session, "premise: both are in their first session");
        }
    }

    [Fact]
    public async Task ReconcileWithSnapshot_ShouldStampEveryAdmittedChannelWithTheRequestMark_WhenTheWindowWasOpenedBeforeStatus()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        await using var run = await Run.StartAsync(peer);
        var added = new ConcurrentQueue<AsteriskChannel>();
        run.Server.Channels.ChannelAdded += added.Enqueue;
        var beforeRequest = run.Server.ReadSessionLossMark();

        peer.StatusChannels = OneCallTwoLegs();
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var origins = added.Select(c => c.AdmittedFrom).ToList();

        using (new AssertionScope())
        {
            added.Select(c => c.UniqueId).Should().BeEquivalentTo(["1700000000.1", "1700000000.2"],
                "premise: the reconciliation admitted both legs from its snapshot");
            added.Should().OnlyContain(c => c.AdmittedFromSnapshot, "premise: both were admitted from the snapshot");
            origins.Should().OnlyContain(o => o != null, "a snapshot admission over the SDK's connection carries its request mark");
            origins.Should().OnlyContain(o => ReferenceEquals(o, origins[0]),
                "one origin is sampled per reconciliation, before Status is sent, and every admission carries it");
            origins[0]!.Mark.Should().Be(beforeRequest!.Value,
                "nothing was delivered between the read before the request and the window's sample");
            origins[0]!.Mark.ConnectionId.Should().NotBe(Guid.Empty, "the mark names the connection that read it");
        }
    }

    [Fact]
    public async Task OnNewChannel_ShouldLeaveAdmittedFromNull_WhenTheArrivalIsLive()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [] };
        await using var run = await Run.StartAsync(peer);
        var added = new TaskCompletionSource<AsteriskChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.Channels.ChannelAdded += channel => added.TrySetResult(channel);

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel("1700000000.9", "PJSIP/1009-00000009", "1700000000.9"));
        var channel = await added.Task.WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            channel.AdmittedFromSnapshot.Should().BeFalse("premise: the channel arrived live");
            channel.AdmittedFrom.Should().BeNull("a live arrival is raised inside its own event's dispatch and carries no sampled mark");
        }
    }

    [Fact]
    public async Task ReconcileWithSnapshot_ShouldMarkTheAdmissionAsSnapshotWithNoOrigin_WhenTheConnectionIsNotAmiConnection()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.ConnectAsync(peer);
        await using var server = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection), NullLogger<VerbaraServer>.Instance)
        {
            TimeProvider = run.Clock,
        };
        var added = new ConcurrentQueue<AsteriskChannel>();
        server.Channels.ChannelAdded += added.Enqueue;

        await server.StartAsync().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            added.Select(c => c.UniqueId).Should().BeEquivalentTo(["1700000000.1", "1700000000.2"],
                "premise: the start's snapshot admitted both legs through the wrapper");
            added.Should().OnlyContain(c => c.AdmittedFromSnapshot,
                "the snapshot provenance is its own member, not derived from the origin");
            added.Should().OnlyContain(c => c.AdmittedFrom == null,
                "a connection that is not the SDK's yields no request mark, and no default mark stands in for one");
        }
    }
}
