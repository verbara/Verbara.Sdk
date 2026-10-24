using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// A reporter starts changing a channel as soon as its admission publishes the instance, rather than
/// waiting for the admitting call to return. Both admission routes must still be announced first.
/// </summary>
public sealed class ChannelEventOrderAcrossThreadsTests
{
    private const int Pairs = 8;
    private const int ChannelsPerPair = 2_000;
    private static readonly TimeSpan RunBound = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void ChannelManager_ShouldRaiseEachChannelsEventsInOrder_WhenOtherThreadsChangeNewlyVisibleChannels()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        var observed = new ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.Ordinal);
        var callbackCount = 0;
        var liveAdmissions = 0;
        var snapshotAdmissions = 0;
        var workerFailures = new ConcurrentQueue<Exception>();
        var joined = new List<bool>();
        using var deadline = new CancellationTokenSource(RunBound);
        using var start = new Barrier(Pairs * 2);
        var rounds = Enumerable.Range(0, Pairs).Select(_ => new Barrier(2)).ToArray();

        manager.ChannelAdded += channel => Record(channel, "Added");
        manager.ChannelStateChanged += channel => Record(channel, "State");
        manager.ChannelDialBegin += channel => Record(channel, "DialBegin");
        manager.ChannelHeld += channel => Record(channel, "Held");
        manager.ChannelUnheld += channel => Record(channel, "Unheld");
        manager.ChannelDialEnd += channel => Record(channel, "DialEnd");
        manager.ChannelRemoved += channel => Record(channel, "Removed");

        var workers = new List<Thread>(Pairs * 2);
        for (var pair = 0; pair < Pairs; pair++)
        {
            var pairNumber = pair;
            workers.Add(Worker(() =>
            {
                start.SignalAndWait(deadline.Token);
                for (var iteration = 0; iteration < ChannelsPerPair; iteration++)
                {
                    var uniqueId = UniqueId(pairNumber, iteration);
                    var fromSnapshot = iteration % 2 == 0;
                    Admit(manager, uniqueId, fromSnapshot);
                    if (fromSnapshot)
                        Interlocked.Increment(ref snapshotAdmissions);
                    else
                        Interlocked.Increment(ref liveAdmissions);

                    // This limits the backlog to one channel per pair, but does not release the reporter:
                    // it already raced the admission by watching the published index below.
                    rounds[pairNumber].SignalAndWait(deadline.Token);
                }
            }, $"channel-admitter-{pair}"));
            workers.Add(Worker(() =>
            {
                start.SignalAndWait(deadline.Token);
                for (var iteration = 0; iteration < ChannelsPerPair; iteration++)
                {
                    var uniqueId = UniqueId(pairNumber, iteration);
                    var spinner = new SpinWait();
                    while (manager.GetByUniqueId(uniqueId) is null)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        spinner.SpinOnce();
                    }

                    manager.OnNewState(uniqueId, ChannelState.Up);
                    manager.OnDialBegin(uniqueId, "destination-" + uniqueId, "PJSIP/destination", "PJSIP/3000");
                    manager.OnHold(uniqueId, "default");
                    manager.OnUnhold(uniqueId);
                    manager.OnDialEnd(uniqueId, "ANSWER");
                    manager.OnHangup(uniqueId, HangupCause.NormalClearing);
                    rounds[pairNumber].SignalAndWait(deadline.Token);
                }
            }, $"channel-reporter-{pair}"));
        }

        try
        {
            workers.ForEach(worker => worker.Start());
            var remaining = Stopwatch.StartNew();
            foreach (var worker in workers)
            {
                var wait = RunBound - remaining.Elapsed;
                if (wait <= TimeSpan.Zero || !worker.Join(wait))
                {
                    workerFailures.Enqueue(new TimeoutException("channel workers exceeded the shared run bound"));
                    break;
                }
            }
        }
        finally
        {
            deadline.Cancel();
            var cleanup = Stopwatch.StartNew();
            foreach (var worker in workers)
            {
                var wait = CleanupBound - cleanup.Elapsed;
                joined.Add(!worker.IsAlive || wait > TimeSpan.Zero && worker.Join(wait));
            }
            if (joined.All(result => result))
            {
                foreach (var round in rounds)
                {
                    using (round)
                    {
                        // The joined workers no longer use this barrier; dispose it when this scope ends.
                    }
                }
            }
        }

        var wrongSequences = observed
            .Where(pair => !pair.Value.SequenceEqual(["Added", "State", "DialBegin", "Held", "Unheld", "DialEnd", "Removed"]))
            .Select(pair => pair.Key + ": " + string.Join(",", pair.Value))
            .ToArray();

        using var scope = new AssertionScope();
        joined.Should().HaveCount(Pairs * 2).And.OnlyContain(result => result, "every physical worker must be reaped");
        workerFailures.Should().BeEmpty("all waits and worker operations must finish within the shared bound");
        liveAdmissions.Should().Be(Pairs * ChannelsPerPair / 2);
        snapshotAdmissions.Should().Be(Pairs * ChannelsPerPair / 2);
        observed.Should().HaveCount(Pairs * ChannelsPerPair);
        callbackCount.Should().Be(Pairs * ChannelsPerPair * 7);
        wrongSequences.Should().BeEmpty("every channel must be admitted before its six later events, in applied order");
        manager.ChannelCount.Should().Be(0);
        manager.ActiveChannels.Should().BeEmpty();
        var retainedNames = Enumerable.Range(0, Pairs)
            .SelectMany(pair => Enumerable.Range(0, ChannelsPerPair).Select(iteration => UniqueId(pair, iteration)))
            .Where(uniqueId => manager.GetByName(Name(uniqueId)) is not null);
        retainedNames.Should().BeEmpty("the name index must also release every removed channel");

        void Record(AsteriskChannel channel, string kind)
        {
            observed.GetOrAdd(channel.UniqueId, static _ => new ConcurrentQueue<string>()).Enqueue(kind);
            Interlocked.Increment(ref callbackCount);
        }

        Thread Worker(Action action, string name) => new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                workerFailures.Enqueue(ex);
                deadline.Cancel();
            }
        }) { IsBackground = true, Name = name };
    }

    [Theory]
    [InlineData("Hangup", false)]
    [InlineData("Hangup", true)]
    [InlineData("NewState", false)]
    [InlineData("NewState", true)]
    public void ChannelAdded_ShouldFinishEverySubscriberBeforeALaterEvent_WhenAdmissionTakesTwoHundredMilliseconds(
        string operation, bool fromSnapshot)
    {
        const string Id = "slow-admission";
        var manager = new ChannelManager(NullLogger.Instance);
        var order = new ConcurrentQueue<string>();
        var workerFailures = new ConcurrentQueue<Exception>();
        using var entered = new ManualResetEventSlim();
        using var reporting = new ManualResetEventSlim();
        var reportedDuringFirstSubscriber = false;
        var firstSubscriberReturned = false;

        manager.ChannelAdded += _ =>
        {
            entered.Set();
            if (!reporting.Wait(CleanupBound))
                throw new TimeoutException("the independent reporter did not begin its call during admission");
            Thread.Sleep(TimeSpan.FromMilliseconds(200)); // fence-allow: SIMULATED-WORK -- models a 200 ms handler below the one-second turn wait.
            Volatile.Write(ref firstSubscriberReturned, true);
        };
        // Recording here proves the later event follows all admission subscribers, not only handler entry.
        manager.ChannelAdded += _ => order.Enqueue("Added");
        manager.ChannelRemoved += _ => order.Enqueue("Removed");
        manager.ChannelStateChanged += _ => order.Enqueue("State");

        var reporter = new Thread(() =>
        {
            try
            {
                if (!entered.Wait(CleanupBound))
                    throw new TimeoutException("the admission subscriber never ran");
                reportedDuringFirstSubscriber = !Volatile.Read(ref firstSubscriberReturned);
                reporting.Set();
                if (operation == "Hangup")
                    manager.OnHangup(Id, HangupCause.NormalClearing);
                else
                    manager.OnNewState(Id, ChannelState.Up);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                workerFailures.Enqueue(ex);
                reporting.Set();
            }
        }) { IsBackground = true, Name = "slow-admission-reporter" };

        reporter.Start();
        Exception? admissionFailure;
        var workerJoined = false;
        try
        {
            admissionFailure = Record.Exception(() => Admit(manager, Id, fromSnapshot));
        }
        finally
        {
            entered.Set();
            reporting.Set();
            workerJoined = reporter.Join(CleanupBound);
        }

        using var scope = new AssertionScope();
        workerJoined.Should().BeTrue("the independent reporter must be reaped even if admission fails");
        workerFailures.Should().BeEmpty();
        admissionFailure.Should().BeNull();
        reportedDuringFirstSubscriber.Should().BeTrue("the later call must race the intentionally slow admission");
        order.Should().Equal(["Added", operation == "Hangup" ? "Removed" : "State"],
            "the later event must follow the second admission subscriber despite the independent racing call");
    }

    private static string UniqueId(int pair, int iteration) => $"order-{pair}-{iteration}";

    private static string Name(string uniqueId) => "PJSIP/" + uniqueId;

    private static void Admit(ChannelManager manager, string uniqueId, bool fromSnapshot)
    {
        if (fromSnapshot)
        {
            using var window = manager.OpenReadWindow();
            // Other pairs' channels are unrelated to this admission-only snapshot.
            manager.ReconcileWithSnapshot([new ChannelSnapshotEntry(uniqueId, Name(uniqueId), ChannelState.Ringing)],
                window, removeAbsent: false);
        }
        else
        {
            manager.OnNewChannel(uniqueId, Name(uniqueId), ChannelState.Ringing);
        }
    }
}
