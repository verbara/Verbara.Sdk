using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>Delivery boundaries, exception ownership and the outbox's existing delayed-drain limit.</summary>
public sealed class ChannelEventDeliveryTests
{
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void ChannelAdded_ShouldFinishEverySubscriberBeforeReportingAHandlerCall_WhenTheHandlerHangsUpAnotherChannel()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("held", "PJSIP/held", ChannelState.Up);
        var order = new List<string>();
        manager.ChannelAdded += _ =>
        {
            order.Add("first-enter");
            manager.OnHangup("held", HangupCause.NormalClearing);
            order.Add("first-return");
        };
        manager.ChannelAdded += _ =>
        {
            order.Add("second-enter");
            order.Add("second-return");
        };
        manager.ChannelRemoved += _ => order.Add("removed");

        manager.OnNewChannel("new", "PJSIP/new", ChannelState.Ringing);
        order.Add("outer-return");

        order.Should().Equal(["first-enter", "first-return", "second-enter", "second-return", "removed", "outer-return"],
            "a handler's call is reported after all admission subscribers return and before the outer call returns");
        manager.GetByUniqueId("held").Should().BeNull();
        manager.GetByUniqueId("new").Should().NotBeNull();
    }

    [Fact]
    public void OnNewState_ShouldRethrowOnlyItsOwnFailureAfterItsNotifications_WhenAnotherPhysicalCallerChangesAChannel()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("failing", "PJSIP/failing", ChannelState.Ringing);
        manager.OnNewChannel("other", "PJSIP/other", ChannelState.Ringing);
        var expectedFailure = new InvalidOperationException("state-handler-failure");
        using var handlerEntered = new ManualResetEventSlim();
        using var unrelatedRaised = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        Exception? causingFailure = null;
        Exception? unrelatedFailure = null;
        var unrelatedObservedInsideHandler = false;
        var heldBeforeRethrow = false;
        var causingThreadId = 0;
        var unrelatedThreadId = 0;
        var failingCallbackThreadId = 0;
        var unrelatedCallbackThreadId = 0;
        var ownLaterEventCount = 0;

        manager.ChannelStateChanged += channel =>
        {
            if (channel.UniqueId == "other")
            {
                unrelatedCallbackThreadId = Environment.CurrentManagedThreadId;
                order.Enqueue("other-state");
                unrelatedRaised.Set();
                return;
            }

            // Reaching the handler proves the causing drain already claimed this notification.
            failingCallbackThreadId = Environment.CurrentManagedThreadId;
            order.Enqueue("failing-state");
            handlerEntered.Set();
            unrelatedObservedInsideHandler = unrelatedRaised.Wait(WaitBound);
            manager.OnHold("failing", "default");
            throw expectedFailure;
        };
        manager.ChannelHeld += channel =>
        {
            if (channel.UniqueId == "failing")
            {
                Interlocked.Increment(ref ownLaterEventCount);
                order.Enqueue("own-held");
            }
        };

        var causing = new Thread(() =>
        {
            causingThreadId = Environment.CurrentManagedThreadId;
            try
            {
                manager.OnNewState("failing", ChannelState.Up);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                heldBeforeRethrow = Volatile.Read(ref ownLaterEventCount) == 1;
                causingFailure = ex;
                order.Enqueue("causing-catch");
            }
        }) { IsBackground = true, Name = "channel-causing-call" };
        var unrelated = new Thread(() =>
        {
            unrelatedThreadId = Environment.CurrentManagedThreadId;
            try
            {
                if (!handlerEntered.Wait(WaitBound))
                    throw new TimeoutException("the causing state handler did not enter");
                manager.OnNewState("other", ChannelState.Up);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                unrelatedFailure = ex;
            }
        }) { IsBackground = true, Name = "channel-unrelated-call" };

        bool causingJoined;
        bool unrelatedJoined;
        try
        {
            // Both starts are outside the handler: the unrelated caller cannot inherit its outbox frame.
            causing.Start();
            unrelated.Start();
            causing.Join(WaitBound + WaitBound);
            unrelated.Join(WaitBound);
        }
        finally
        {
            handlerEntered.Set();
            unrelatedRaised.Set();
            causingJoined = !causing.IsAlive || causing.Join(WaitBound);
            unrelatedJoined = !unrelated.IsAlive || unrelated.Join(WaitBound);
        }

        using var scope = new AssertionScope();
        causingJoined.Should().BeTrue("the causing physical caller must be reaped");
        unrelatedJoined.Should().BeTrue("the unrelated physical caller must be reaped");
        unrelatedObservedInsideHandler.Should().BeTrue("the unrelated event must not be stranded behind the failing handler");
        causingFailure.Should().BeSameAs(expectedFailure);
        unrelatedFailure.Should().BeNull("another call must not receive the causing handler's exception");
        heldBeforeRethrow.Should().BeTrue("the causing call must deliver its handler's later notification before rethrowing");
        ownLaterEventCount.Should().Be(1);
        causingThreadId.Should().NotBe(unrelatedThreadId);
        failingCallbackThreadId.Should().Be(causingThreadId);
        unrelatedCallbackThreadId.Should().Be(unrelatedThreadId);
        order.Should().Equal("failing-state", "other-state", "own-held", "causing-catch");
    }

    [Fact]
    public void Drain_ShouldAdoptAPausedLiveCallersNotificationAndReportItsFailure_WhenAnotherCallReachesTheDefaultTurnWait()
    {
        var expectedFailure = new InvalidOperationException("paused-caller-handler-failure");
        var reported = new ConcurrentQueue<Exception>();
        var order = new ConcurrentQueue<string>();
        var outbox = new LiveEventOutbox(reported.Enqueue, LiveEventOutbox.DefaultTurnWait);
        using var postedByA = new ManualResetEventSlim();
        using var resumeA = new ManualResetEventSlim();
        using var finishedB = new ManualResetEventSlim();
        Exception? failureA = null;
        Exception? failureB = null;
        var threadIdA = 0;
        var threadIdB = 0;
        var adoptedCallbackThreadId = 0;
        var ownCallbackThreadId = 0;
        var aliveAndPausedWhenAdopted = false;
        var bFinishedBeforeAResumed = false;
        var aWasAliveBeforeResume = false;
        Thread? callerA = null;
        callerA = new Thread(() =>
        {
            threadIdA = Environment.CurrentManagedThreadId;
            try
            {
                outbox.Post(() =>
                {
                    adoptedCallbackThreadId = Environment.CurrentManagedThreadId;
                    aliveAndPausedWhenAdopted = callerA is { IsAlive: true } && !resumeA.IsSet;
                    order.Enqueue("adopted-A");
                    throw expectedFailure;
                });
                postedByA.Set();
                if (!resumeA.Wait(WaitBound + WaitBound))
                    throw new TimeoutException("caller A was not released after caller B's drain");
                outbox.Drain();
                order.Enqueue("A-drained");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failureA = ex;
            }
        }) { IsBackground = true, Name = "outbox-paused-caller-A" };
        var callerB = new Thread(() =>
        {
            threadIdB = Environment.CurrentManagedThreadId;
            try
            {
                if (!postedByA.Wait(WaitBound))
                    throw new TimeoutException("caller A did not post before caller B");
                outbox.Post(() =>
                {
                    ownCallbackThreadId = Environment.CurrentManagedThreadId;
                    order.Enqueue("own-B");
                });
                outbox.Drain();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failureB = ex;
            }
            finally
            {
                finishedB.Set();
            }
        }) { IsBackground = true, Name = "outbox-adopting-caller-B" };

        bool joinedA;
        bool joinedB;
        try
        {
            callerA.Start();
            callerB.Start();
            bFinishedBeforeAResumed = finishedB.Wait(WaitBound);
            aWasAliveBeforeResume = callerA.IsAlive;
        }
        finally
        {
            // Keep A alive and before Drain until B is done; release it even when coordination fails.
            resumeA.Set();
            joinedA = !callerA.IsAlive || callerA.Join(WaitBound);
            joinedB = !callerB.IsAlive || callerB.Join(WaitBound);
        }

        using var scope = new AssertionScope();
        joinedA.Should().BeTrue("the original physical caller must be reaped");
        joinedB.Should().BeTrue("the adopting physical caller must be reaped");
        bFinishedBeforeAResumed.Should().BeTrue();
        aWasAliveBeforeResume.Should().BeTrue("this is an active paused caller, not an abandoned post");
        aliveAndPausedWhenAdopted.Should().BeTrue();
        threadIdA.Should().NotBe(threadIdB);
        adoptedCallbackThreadId.Should().Be(threadIdB);
        ownCallbackThreadId.Should().Be(threadIdB);
        reported.Should().ContainSingle().Which.Should().BeSameAs(expectedFailure);
        failureA.Should().BeNull("the original caller's resumed drain does not receive an adopted failure");
        failureB.Should().BeNull("the adopting caller reports an unowned failure without receiving it");
        order.Should().Equal("adopted-A", "own-B", "A-drained");
    }
}
