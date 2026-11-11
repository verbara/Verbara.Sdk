using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The attachment generation a session notification's loss mark carries (<see cref="SessionEventEpoch.Attachment"/>):
/// a server detached keeping its calls and attached again with the same instance carries a new generation, so a
/// notification of the new attachment differs from one of the old across the detach gap; attaching the same instance
/// again while it is still attached changes nothing; and a handler of the detached attachment that raises its
/// notification after the re-attach keeps its own generation, never one looked up by server id or instance.
/// </summary>
/// <remarks>
/// A real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> over <see cref="Run"/>: every frame goes over the wire
/// and through the connection's pump. The concurrency test is gate-ordered: the arrival parks at an engine step, sets
/// its entered signal, and the test detaches and re-attaches only once it has. Every wait is bounded by
/// <see cref="Run.Bound"/>.
/// </remarks>
public sealed class SessionEventEpochAttachmentTests
{
    private const string ServerId = "srv-epoch";
    private const string Caller = "1700000600.1";
    private const string CallerChannel = "PJSIP/pstn-00000601";
    private const string Other = "1700000600.2";
    private const string OtherChannel = "PJSIP/pstn-00000602";

    [Fact]
    public async Task Events_ShouldDifferInAttachment_WhenTheSameInstanceIsDetachedAndReattached()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        manager.DetachFromServer(ServerId, DetachedCalls.Keep);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().NotBeNull("premise: the call started over the SDK's connection");
            ended.Epoch.Should().NotBeNull("the call ended over the SDK's connection, attached again");
            (ended.Epoch?.ConnectionId, ended.Epoch?.AmiSession, ended.Epoch?.DroppedBefore).Should().Be(
                (started.Epoch?.ConnectionId, started.Epoch?.AmiSession, started.Epoch?.DroppedBefore),
                "premise: the same connection instance, in the same session, dropped nothing");
            ended.Epoch?.Attachment.Should().NotBe(started.Epoch?.Attachment ?? 0,
                "a detach and a real re-attach of the same instance leave a gap the engine did not watch, so the new "
                + "attachment's notification carries a new generation");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                "the engine was detached from the server between the call's start and its end");
        }
    }

    [Fact]
    public async Task Events_ShouldDifferInAttachment_WhenANewManagerAttachesTheSameInstanceAfterTheFirstIsDisposed()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        var first = EpochRig.NewManager(new FakeTimeProvider());
        var firstRecorder = EpochRecorder.On(first);
        first.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var before = await firstRecorder.WaitAsync<CallStartedEvent>();
        await first.DisposeAsync();

        await using var second = EpochRig.NewManager(new FakeTimeProvider());
        var secondRecorder = EpochRecorder.On(second);
        second.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
        var after = await secondRecorder.WaitAsync<CallStartedEvent>();

        using (new AssertionScope())
        {
            before.Epoch.Should().NotBeNull("premise: the first manager's call started over the SDK's connection");
            after.Epoch.Should().NotBeNull("the second manager's call started over the same connection");
            (after.Epoch?.ConnectionId, after.Epoch?.AmiSession, after.Epoch?.DroppedBefore).Should().Be(
                (before.Epoch?.ConnectionId, before.Epoch?.AmiSession, before.Epoch?.DroppedBefore),
                "premise: the same connection instance, in the same session, dropped nothing");
            after.Epoch?.Attachment.Should().NotBe(before.Epoch?.Attachment ?? 0,
                "no manager watched the server between the first manager's disposal and the second's attach, so the "
                + "second attachment carries a generation no earlier attachment in the process carried");
            SessionEventEpoch.LossBetween(before.Epoch, after.Epoch).Should().Be(SessionEventLoss.Possible,
                "a gap no engine watched lies between the two notifications");
        }
    }

    [Fact]
    public async Task Events_ShouldCarryEqualAttachment_WhenTheSameInstanceIsAttachedAgainWhileAttached()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().NotBeNull("premise: the call started over the SDK's connection");
            ended.Epoch.Should().Be(started.Epoch,
                "attaching the instance already attached is a no-op: the engine never stopped watching the server");
            SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                "no reconnect, drop or detach lies between the call's start and its end");
        }
    }

    [Fact]
    public async Task CallStarted_ShouldKeepTheEarlierAttachment_WhenAKeepDetachedArrivalRaisesAfterTheReattach()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(run.Server, ServerId);
        // A call raised by the first attachment, for its generation.
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Other, OtherChannel, Other));
        var reference = await recorder.WaitAsync<CallStartedEvent>();

        using var park = new NotificationGate();
        manager.StepHook = (step, _, id) =>
        {
            if (step == HandlerStep.ArrivalPublished && id == Caller)
                park.Hold();
        };
        try
        {
            await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
            await park.Entered.Task.WaitAsync(Run.Bound);
            manager.StepHook = null;
            // Off the parked dispatch thread: the detach keeps the call and the same instance is attached again.
            await Task.Run(() =>
            {
                manager.DetachFromServer(ServerId, DetachedCalls.Keep);
                manager.AttachToServer(run.Server, ServerId);
            }).WaitAsync(Run.Bound);
            var raisedBeforeRelease = recorder.Seen.Any(s =>
                s.Notification is CallStartedEvent && s.Notification.SessionId != reference.SessionId);
            park.Release();
            var started = await recorder.WaitAsync<CallStartedEvent>(e => e.SessionId != reference.SessionId);
            await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
            var ended = await recorder.WaitAsync<CallEndedEvent>(e => e.SessionId == started.SessionId);

            using (new AssertionScope())
            {
                raisedBeforeRelease.Should().BeFalse("premise: the arrival was parked before its call-started");
                reference.Epoch.Should().NotBeNull("premise: the first attachment raised over the SDK's connection");
                started.Epoch.Should().NotBeNull("the parked arrival raised over the SDK's connection");
                started.Epoch?.Attachment.Should().Be(reference.Epoch?.Attachment ?? -1,
                    "the arrival is a handler of the detached attachment, so its notification carries that attachment's "
                    + "generation, also when it is raised after the re-attach");
                started.Epoch.Should().Be(reference.Epoch,
                    "the same attachment, connection and session, with no drop between the two arrivals");
                ended.Epoch.Should().NotBeNull("the new attachment raised the ending over the SDK's connection");
                (ended.Epoch?.ConnectionId, ended.Epoch?.AmiSession, ended.Epoch?.DroppedBefore).Should().Be(
                    (started.Epoch?.ConnectionId, started.Epoch?.AmiSession, started.Epoch?.DroppedBefore),
                    "premise: the same connection instance, in the same session, dropped nothing");
                ended.Epoch?.Attachment.Should().NotBe(started.Epoch?.Attachment ?? 0,
                    "the ending was raised by the new attachment, whose generation differs from the detached one's");
                SessionEventEpoch.LossBetween(started.Epoch, ended.Epoch).Should().Be(SessionEventLoss.Possible,
                    "the engine was detached from the server between the call's start and its end");
            }
        }
        finally
        {
            manager.StepHook = null;
            park.Release();
        }
    }
}
