using System.Collections.Concurrent;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Xunit.Abstractions;

namespace Verbara.Sdk.Live.Tests.Server;

public sealed class ReentrantAttachmentProtocolTests(ITestOutputHelper output)
{
    private static readonly string[] _expectedRefusals = ["a", "b"];
    [Fact]
    public async Task DetachFromServer_ShouldReturnWithoutWaiting_WhenASubscriberHoldsACallsLockOfTheServer()
    {
        await using var rig = await EndingBaselineRig.CreateAsync();
        rig.Attach();
        rig.Add("X");
        var call = rig.Manager.GetByLinkedId("server", "L")!;
        call.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        using var subscriber = rig.Manager.Events.Subscribe(new Observer(e =>
        {
            if (e is CallHeldEvent held && held.SessionId == call.SessionId) gate.Hold();
        }));
        var workers = new List<Task>();
        var returnedBeforeRelease = false;
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => rig.Run.Server.Channels.OnHold("X", null)));
            await gate.Entered.Task.WaitAsync(EndingBaselineRig.Bound);
            var detach = Task.Run(() => rig.Manager.DetachFromServer("server", DetachedCalls.Keep));
            workers.Add(detach);
            // fence-allow: GUARD-TIMEOUT — detects eager Keep waiting while the subscriber deliberately holds the call lock.
            var error = await Record.ExceptionAsync(() => detach.WaitAsync(TimeSpan.FromSeconds(2)));
            returnedBeforeRelease = error is null;
            output.WriteLine("Keep returned before releasing subscriber: " + returnedBeforeRelease);
            // RunAsync's finally releases the subscriber and joins BOTH producers even when this observation is red.
        });

        returnedBeforeRelease.Should().BeTrue("Keep must not walk the held call locks");
        call.Unverified.Should().BeFalse("Keep must leave the observation record alone");
        call.State.Should().Be(CallSessionState.OnHold);
        rig.Manager.GetById(call.SessionId).Should().BeSameAs(call);
        rig.Manager.GetByChannelId("server", "X").Should().BeSameAs(call);
        rig.Ends(call).Should().Be(0);
        rig.Attach();
        rig.Hangup("X");
        rig.Ends(call).Should().Be(1);
        call.HangupCause.Should().Be(HangupCause.NormalClearing);
        call.Metadata.Should().NotContainKey("cause");
    }

    [Fact]
    public async Task AttachToServer_ShouldNotWaitForAnotherCallsLock_WhenTwoSubscribersReattachEachOthersServers()
    {
        await using var a = await EndingBaselineRig.CreateAsync();
        await using var b = await EndingBaselineRig.CreateAsync();
        await using var replacementA = await EndingBaselineRig.CreateAsync();
        await using var replacementB = await EndingBaselineRig.CreateAsync();
        var manager = a.Manager;
        manager.AttachToServer(a.Run.Server, "a");
        manager.AttachToServer(b.Run.Server, "b");
        a.Add("A", "LA");
        b.Add("B", "LB");
        var callA = manager.GetByChannelId("a", "A")!;
        var callB = manager.GetByChannelId("b", "B")!;
        callA.State = CallSessionState.Connected;
        callB.State = CallSessionState.Connected;
        using var gate = new NotificationGate();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var refused = new ConcurrentQueue<string>();
        var otherErrors = new ConcurrentQueue<Exception>();
        using var subscriber = manager.Events.Subscribe(new Observer(e =>
        {
            if (e is not CallHeldEvent held || (held.SessionId != callA.SessionId && held.SessionId != callB.SessionId)) return;
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            gate.Hold();
            var id = held.SessionId == callA.SessionId ? "b" : "a";
            var replacement = held.SessionId == callA.SessionId ? replacementB.Run.Server : replacementA.Run.Server;
            try
            {
                manager.DetachFromServer(id, DetachedCalls.Keep);
                manager.AttachToServer(replacement, id);
            }
            catch (InvalidOperationException error) when (
                error.Message.Contains("cannot be attached from inside a notification", StringComparison.Ordinal)
                && error.Message.Contains("'" + id + "'", StringComparison.Ordinal))
            {
                refused.Enqueue(id);
            }
            catch (Exception error) { otherErrors.Enqueue(error); }
        }));
        var workers = new List<Task>();
        await NotificationGate.RunAsync([gate], workers, async () =>
        {
            workers.Add(Task.Run(() => a.Run.Server.Channels.OnHold("A", null)));
            workers.Add(Task.Run(() => b.Run.Server.Channels.OnHold("B", null)));
            await bothEntered.Task.WaitAsync(EndingBaselineRig.Bound);
            gate.Release();
            await Task.WhenAll(workers).WaitAsync(EndingBaselineRig.Bound);
        });

        foreach (var error in otherErrors) output.WriteLine(error.GetType().Name + ": " + error.Message);
        workers.Should().OnlyContain(worker => worker.IsCompletedSuccessfully, "both counterexample producers must drain");
        refused.Should().BeEquivalentTo(_expectedRefusals);
        otherErrors.Should().BeEmpty();
        callA.State.Should().Be(CallSessionState.OnHold);
        callB.State.Should().Be(CallSessionState.OnHold);
        manager.GetById(callA.SessionId).Should().BeSameAs(callA);
        manager.GetById(callB.SessionId).Should().BeSameAs(callB);
        manager.AttachToServer(replacementA.Run.Server, "a");
        manager.AttachToServer(replacementB.Run.Server, "b");
        a.Ends(callA).Should().Be(1);
        a.Ends(callB).Should().Be(1);
        callA.Metadata["cause"].Should().Be("reload");
        callB.Metadata["cause"].Should().Be("reload");
    }

    private sealed class Observer(Action<SessionDomainEvent> next) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
