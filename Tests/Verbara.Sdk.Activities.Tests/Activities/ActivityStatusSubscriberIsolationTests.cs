using System.Diagnostics.Metrics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Verbara.Sdk;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;
using Xunit.Abstractions;

namespace Verbara.Sdk.Activities.Tests.Activities;

/// <summary>
/// An activity's status subscriber's failure is its own: every other subscriber of <c>StatusChanges</c> still receives
/// every status and the completion, the activity ends as it would with no failing subscriber and runs its cancellation
/// work, nothing leaves <c>Subscribe</c>, <c>StartAsync</c>, <c>CancelAsync</c> or <c>DisposeAsync</c>, and each failure
/// is counted once on <c>activities.status.subscriber_faults</c> with the activity's type in <c>activity.type</c>.
/// </summary>
/// <remarks>
/// <para>One row per base class: <see cref="ActivityBase"/>, <see cref="AmiActivityBase"/> and
/// <see cref="AriActivityBase"/> each hold their own subject, and every shipped activity inherits one of them.</para>
/// <para>The counter is read by name, meter and tag, so this class compiles on a tree without it, and only this class's
/// activity types are counted: another class's activities never reach the tally.</para>
/// </remarks>
public sealed class ActivityStatusSubscriberIsolationTests(ITestOutputHelper output)
{
    private const string MeterName = "Verbara.Sdk.Activities";
    private const string SubscriberFaults = "activities.status.subscriber_faults";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> ActivityBases => new()
    {
        nameof(ActivityBase),
        nameof(AmiActivityBase),
        nameof(AriActivityBase),
    };

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldDeliverEveryStatusAndTheCompletionToTheOtherSubscriber_WhenTheFirstThrowsOnEveryNotification(
        string activityBase)
    {
        using var faults = new FaultCounter();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = IsolationActivities.Create(activityBase, async ct =>
        {
            running.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(ct);
        });
        var thrower = new ThrowingObserver();
        var recorder = new RecordingObserver();

        using var subscriptions = new CompositeDisposable();
        var subscribing = Subscribing(subscriptions, () => activity.StatusChanges.Subscribe(thrower));
        using var second = activity.StatusChanges.Subscribe(recorder);
        var start = activity.StartAsync().AsTask();
        await Task.WhenAny(running.Task, start).WaitAsync(SignalTimeout);
        var cancelling = await Record.ExceptionAsync(() => activity.CancelAsync().AsTask());
        var starting = await Record.ExceptionAsync(() => start.WaitAsync(SignalTimeout));
        var statusAfterCancel = activity.Status;
        var disposing = await Record.ExceptionAsync(() => activity.DisposeAsync().AsTask());

        using var scope = new AssertionScope();
        subscribing.Should().BeNull("a status subscriber's failure is its own: the replay of Pending does not leave Subscribe");
        starting.Should().BeNull("a status subscriber's failure is its own: StartAsync does not see it");
        cancelling.Should().BeNull("a status subscriber's failure is its own: CancelAsync returns normally");
        disposing.Should().BeNull("a status subscriber's failure is its own: DisposeAsync returns normally");
        recorder.Statuses.Should().Equal(
            [ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Cancelled],
            "the second subscriber receives every status through Cancelled");
        recorder.Completions.Should().Be(1, "the second subscriber receives the completion at the disposal");
        statusAfterCancel.Should().Be(ActivityStatus.Cancelled, "the activity ends as it would with no failing subscriber");
        IsolationActivities.CancellationWorkRan(activity).Should().BeTrue(
            "the activity runs its cancellation work whatever its subscriber threw on Cancelled");
        thrower.Calls.Should().Be(5, "the first subscriber stays subscribed: four statuses and the completion");
        faults.Count(activity).Should().Be(5, "one count per status and completion the first subscriber threw on");
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldCountTheFailureUnderTheActivityType_WhenASubscriberThrowsOnAStatus(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);
        var calls = 0;

        using var subscriptions = new CompositeDisposable();
        var subscribing = Subscribing(subscriptions, () => activity.StatusChanges.Subscribe(status =>
        {
            calls++;
            if (status == ActivityStatus.Completed)
                throw new InvalidOperationException("completed-handler");
        }));
        var starting = await Record.ExceptionAsync(() => activity.StartAsync().AsTask());
        var status = activity.Status;
        await activity.DisposeAsync();

        using var scope = new AssertionScope();
        subscribing.Should().BeNull();
        starting.Should().BeNull("a status subscriber's failure is its own: an activity that completed does not fail");
        status.Should().Be(ActivityStatus.Completed);
        calls.Should().Be(4, "Pending, Starting, InProgress and Completed");
        faults.Tags.Should().Equal(
            [activity.GetType().Name],
            "one count, tagged activity.type with the concrete activity's type name");
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldEndOnlyTheComposedSubscriptionAndCountItOnce_WhenAHandlerBehindWhereThrows(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);
        var recorder = new RecordingObserver();
        using var direct = activity.StatusChanges.Subscribe(recorder);
        var composedCalls = 0;

        using var subscriptions = new CompositeDisposable();
        var subscribing = Subscribing(subscriptions, () => activity.StatusChanges.Where(_ => true).Subscribe(_ =>
        {
            composedCalls++;
            throw new InvalidOperationException("composed-handler");
        }));
        var starting = await Record.ExceptionAsync(() => activity.StartAsync().AsTask());
        await activity.DisposeAsync();

        using var scope = new AssertionScope();
        subscribing.Should().BeNull("a status subscriber's failure is its own, composed or not");
        starting.Should().BeNull("the publisher throws nothing");
        composedCalls.Should().Be(1, "the operator ends its subscription on the handler's first exception");
        faults.Count(activity).Should().Be(1, "that one failure is counted once");
        recorder.Statuses.Should().Equal(
            [ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Completed],
            "the direct subscriber receives every status");
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldDeliverEveryStatusAndCountNothing_WhenNoSubscriberThrows(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        using var a = activity.StatusChanges.Subscribe(first);
        using var b = activity.StatusChanges.Subscribe(second);

        await activity.StartAsync();
        await activity.DisposeAsync();

        using var scope = new AssertionScope();
        ActivityStatus[] expected =
            [ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Completed];
        first.Statuses.Should().Equal(expected);
        second.Statuses.Should().Equal(expected);
        first.Completions.Should().Be(1);
        second.Completions.Should().Be(1);
        faults.Count(activity).Should().Be(0, "no subscriber failed");
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsIt(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);

        var subscribing = () => activity.StatusChanges.Subscribe(_ => throw OutOfMemory());

        subscribing.Should().Throw<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        faults.Count(activity).Should().Be(0, "an out-of-memory failure is not a subscriber fault");
        await Record.ExceptionAsync(() => activity.DisposeAsync().AsTask());
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task DisposeAsync_ShouldLetOutOfMemoryThrough_WhenASubscriberThrowsItOnTheCompletion(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);
        using var subscription = activity.StatusChanges.Subscribe(_ => { }, () => throw OutOfMemory());

        var disposing = () => activity.DisposeAsync().AsTask();

        await disposing.Should().ThrowAsync<OutOfMemoryException>("the process cannot go on, so no guard hides it");
        faults.Count(activity).Should().Be(0, "an out-of-memory failure is not a subscriber fault");
    }

    /// <summary>
    /// Up to 2.10.x a throw on <c>InProgress</c> left the base's <c>SetStatus</c> inside the execution's <c>try</c>, so
    /// the activity ended <c>Failed</c> without ever executing. The subscriber's failure is now its own: the activity
    /// executes (its side effects happen) and ends <c>Completed</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StartAsync_ShouldExecuteAndComplete_WhenASubscriberThrowsOnInProgress(string activityBase)
    {
        using var faults = new FaultCounter();
        var executions = 0;
        var activity = IsolationActivities.Create(activityBase, _ =>
        {
            Interlocked.Increment(ref executions);
            return ValueTask.CompletedTask;
        });
        using var subscription = activity.StatusChanges.Subscribe(status =>
        {
            if (status == ActivityStatus.InProgress)
                throw new InvalidOperationException("in-progress-handler");
        });

        var starting = await Record.ExceptionAsync(() => activity.StartAsync().AsTask());
        var status = activity.Status;
        await activity.DisposeAsync();

        using var scope = new AssertionScope();
        starting.Should().BeNull("a status subscriber's failure is its own: StartAsync does not see it");
        Volatile.Read(ref executions).Should().Be(1, "the activity executes whatever its subscriber threw on InProgress");
        status.Should().Be(ActivityStatus.Completed, "the activity ends as it would with no failing subscriber, not Failed");
        faults.Count(activity).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StatusChanges_ShouldThrowArgumentNullAndCountNothing_WhenTheObserverIsNull(string activityBase)
    {
        using var faults = new FaultCounter();
        var activity = IsolationActivities.Create(activityBase, _ => ValueTask.CompletedTask);

        var subscribing = () => activity.StatusChanges.Subscribe(null!);

        subscribing.Should().Throw<ArgumentNullException>("a null observer is the caller's error, as on the subject itself");
        faults.Count(activity).Should().Be(0, "no subscriber exists, so the replay of Pending is no subscriber's failure");
        await activity.DisposeAsync();
    }

    /// <summary>
    /// The arm no activity reaches: nothing raises <c>OnError</c> on an activity's subject, and the guard still has to
    /// keep what a subscriber throws on it away from the next subscriber.
    /// </summary>
    [Fact]
    public void OnError_ShouldReachTheNextSubscriberAndCountOnce_WhenASubscriberThrowsOnIt()
    {
        using var faults = new FaultCounter();
        using var subject = new Subject<ActivityStatus>();
        var stream = new GuardedStatusStream(subject, nameof(IsolationAgiActivity));
        var next = new RecordingObserver();
        using var first = stream.Subscribe(new ThrowingObserver());
        using var second = stream.Subscribe(next);
        var raised = new InvalidOperationException("the stream failed");

        var raising = Record.Exception(() => subject.OnError(raised));

        using var scope = new AssertionScope();
        raising.Should().BeNull("a subscriber's failure on the error is its own");
        next.Error.Should().BeSameAs(raised, "the next subscriber receives the error");
        faults.Tags.Should().Equal([nameof(IsolationAgiActivity)], "one count, under the stream's activity type");
    }

    /// <summary>
    /// The execution ends on a thread-pool thread at the moment the test cancels the activity on its own thread, while
    /// the first subscriber throws on every notification: Completed raised by the execution races Cancelled raised by
    /// <c>CancelAsync</c>, and the disposal's completion follows. Whichever ending wins, the second subscriber sees it
    /// once, last, and then the completion; every failure is counted once; and no call throws.
    /// </summary>
    /// <remarks>
    /// Cancelled wins almost every round here (the release has to reach a thread-pool thread first): the split is
    /// written to the test output. The leg below takes the other side of the race.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task CancelAsync_ShouldDeliverOneEndingAndTheCompletionToTheOtherSubscriber_WhenTheExecutionEndsOnAnotherThreadAsItIsCancelled(
        string activityBase)
    {
        var race = await RaceEndingsAsync(activityBase, RaceStart.ReleaseConcurrently);

        race.Outcomes.Should().BeEmpty(
            "a status subscriber's failure is its own on every round, whichever ending wins the race ({0})", race.Split);
    }

    /// <summary>
    /// The other side of the race above: the test releases the execution and waits until its body has returned, so
    /// the base is between the end of <c>ExecuteAsync</c> and the lock it takes to raise Completed when
    /// <c>CancelAsync</c> takes the same lock. The same rules hold on every round; and since the execution has
    /// already returned, Completed has to win some rounds, which is what the leg is for.
    /// </summary>
    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task CancelAsync_ShouldDeliverOneEndingAndTheCompletionToTheOtherSubscriber_WhenTheExecutionHasJustReturnedAsItIsCancelled(
        string activityBase)
    {
        var race = await RaceEndingsAsync(activityBase, RaceStart.ReleaseAndWaitForTheBodyToReturn);

        using var scope = new AssertionScope();
        race.Outcomes.Should().BeEmpty(
            "a status subscriber's failure is its own on every round, whichever ending wins the race ({0})", race.Split);
        race.CompletedWins.Should().BePositive(
            "the leg exists to exercise the Completed side of the race ({0})", race.Split);
    }

    private enum RaceStart
    {
        ReleaseConcurrently,
        ReleaseAndWaitForTheBodyToReturn,
    }

    private sealed record RaceResult(IReadOnlyList<string> Outcomes, int CompletedWins, int CancelledWins)
    {
        public string Split => $"Completed won {CompletedWins}, Cancelled won {CancelledWins}";
    }

    private async Task<RaceResult> RaceEndingsAsync(string activityBase, RaceStart raceStart)
    {
        const int Rounds = 50;
        using var faults = new FaultCounter();
        var outcomes = new List<string>();
        var completedWins = 0;
        var cancelledWins = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = 0;
            var activity = IsolationActivities.Create(activityBase, async _ =>
            {
                running.TrySetResult();
                // Released on the thread pool: the execution ends there, racing the test's CancelAsync.
                await release.Task.ConfigureAwait(false);
                Volatile.Write(ref returned, 1);
            });
            var thrower = new ThrowingObserver();
            var recorder = new RecordingObserver();
            var before = faults.Count(activity);
            using var subscriptions = new CompositeDisposable();
            var subscribing = Subscribing(subscriptions, () => activity.StatusChanges.Subscribe(thrower));
            using var second = activity.StatusChanges.Subscribe(recorder);

            var start = activity.StartAsync().AsTask();
            await Task.WhenAny(running.Task, start).WaitAsync(SignalTimeout);
            Task ending;
            if (raceStart == RaceStart.ReleaseConcurrently)
            {
                ending = Task.Run(release.TrySetResult);
            }
            else
            {
                release.TrySetResult();
                SpinWait.SpinUntil(() => Volatile.Read(ref returned) == 1, SignalTimeout);
                ending = Task.CompletedTask;
            }

            var cancelling = await Record.ExceptionAsync(() => activity.CancelAsync().AsTask());
            await ending.WaitAsync(SignalTimeout);
            var startEnd = await Record.ExceptionAsync(() => start.WaitAsync(SignalTimeout));
            var finalStatus = activity.Status;
            var disposing = await Record.ExceptionAsync(() => activity.DisposeAsync().AsTask());

            if (finalStatus == ActivityStatus.Completed)
                completedWins++;
            else if (finalStatus == ActivityStatus.Cancelled)
                cancelledWins++;

            var statuses = recorder.Statuses;
            var delivered = statuses.Count + recorder.Completions;
            var counted = faults.Count(activity) - before;
            var wrong = new List<string>();
            var thrown = subscribing ?? cancelling ?? startEnd ?? disposing;
            if (thrown is not null)
                wrong.Add("a call threw: " + thrown.Message);
            if (statuses.Count != 4 || statuses[3] != finalStatus
                || statuses[3] is not (ActivityStatus.Completed or ActivityStatus.Cancelled))
                wrong.Add("statuses " + string.Join(",", statuses) + " final " + finalStatus);
            if (recorder.Completions != 1)
                wrong.Add("completions " + recorder.Completions);
            if (counted != delivered || thrower.Calls != delivered)
                wrong.Add($"counted {counted} thrown {thrower.Calls} delivered {delivered}");
            if (wrong.Count > 0)
                outcomes.Add($"round {round}: " + string.Join("; ", wrong));
        }

        var result = new RaceResult(outcomes, completedWins, cancelledWins);
        output.WriteLine($"{activityBase} {raceStart}: {result.Split} of {Rounds} rounds");
        return result;
    }

    /// <summary>Subscribes, keeping the subscription in <paramref name="subscriptions"/>; returns what the call threw.</summary>
    private static Exception? Subscribing(CompositeDisposable subscriptions, Func<IDisposable> subscribe)
    {
        try
        {
            subscriptions.Add(subscribe());
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex;
        }
    }

    // The only exception the guard lets through is one only the runtime may throw; a test has to raise it itself.
#pragma warning disable CA1859, CA2201, MA0012
    private static Exception OutOfMemory() => new OutOfMemoryException();
#pragma warning restore CA1859, CA2201, MA0012

    /// <summary>Counts <c>activities.status.subscriber_faults</c> for this class's activity types only.</summary>
    private sealed class FaultCounter : IDisposable
    {
        private static readonly HashSet<string> OwnTypes =
        [
            nameof(IsolationAgiActivity), nameof(IsolationAmiActivity), nameof(IsolationAriActivity),
        ];

        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<string> _tags = [];

        public FaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == SubscriberFaults)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? type = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "activity.type")
                        type = tag.Value as string;
                }

                if (type is null || !OwnTypes.Contains(type))
                    return;
                lock (_gate)
                {
                    for (var i = 0; i < value; i++)
                        _tags.Add(type);
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<string> Tags
        {
            get { lock (_gate) return [.. _tags]; }
        }

        public long Count(IActivity activity)
        {
            var name = activity.GetType().Name;
            lock (_gate) return _tags.Count(t => t == name);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ThrowingObserver : IObserver<ActivityStatus>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnNext(ActivityStatus value)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("status-handler");
        }

        public void OnError(Exception error)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("error-handler");
        }

        public void OnCompleted()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("completion-handler");
        }
    }

    private sealed class RecordingObserver : IObserver<ActivityStatus>
    {
        private readonly Lock _gate = new();
        private readonly List<ActivityStatus> _statuses = [];
        private int _completions;

        public IReadOnlyList<ActivityStatus> Statuses
        {
            get { lock (_gate) return [.. _statuses]; }
        }

        public int Completions => Volatile.Read(ref _completions);

        public void OnNext(ActivityStatus value)
        {
            lock (_gate) _statuses.Add(value);
        }

        public Exception? Error { get; private set; }

        public void OnError(Exception error) => Error = error;

        public void OnCompleted() => Interlocked.Increment(ref _completions);
    }

    private static class IsolationActivities
    {
        public static IActivity Create(string activityBase, Func<CancellationToken, ValueTask> execute) => activityBase switch
        {
            nameof(ActivityBase) => new IsolationAgiActivity(Substitute.For<IAgiChannel>(), execute),
            nameof(AmiActivityBase) => new IsolationAmiActivity(Substitute.For<IAmiConnection>(), execute),
            nameof(AriActivityBase) => new IsolationAriActivity(Substitute.For<IAriClient>(), execute),
            _ => throw new ArgumentOutOfRangeException(nameof(activityBase), activityBase, "no such activity base"),
        };

        /// <summary>
        /// Whether the activity ran its cancellation work: the execution saw its token cancelled, and for an ARI activity
        /// <c>OnCancellingAsync</c> ran too (the step a throw on <c>Cancelled</c> skipped).
        /// </summary>
        public static bool CancellationWorkRan(IActivity activity) => activity switch
        {
            IsolationAriActivity ari => ari.Cancelling && ari.TokenCancelled,
            IsolationAgiActivity agi => agi.TokenCancelled,
            IsolationAmiActivity ami => ami.TokenCancelled,
            _ => false,
        };
    }

    private sealed class IsolationAgiActivity(IAgiChannel channel, Func<CancellationToken, ValueTask> execute) : ActivityBase(channel)
    {
        public bool TokenCancelled { get; private set; }

        protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await execute(cancellationToken);
            }
            finally
            {
                TokenCancelled = cancellationToken.IsCancellationRequested;
            }
        }
    }

    private sealed class IsolationAmiActivity(IAmiConnection ami, Func<CancellationToken, ValueTask> execute) : AmiActivityBase(ami)
    {
        public bool TokenCancelled { get; private set; }

        protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await execute(cancellationToken);
            }
            finally
            {
                TokenCancelled = cancellationToken.IsCancellationRequested;
            }
        }
    }

    private sealed class IsolationAriActivity(IAriClient ariClient, Func<CancellationToken, ValueTask> execute) : AriActivityBase(ariClient)
    {
        public bool TokenCancelled { get; private set; }

        public bool Cancelling { get; private set; }

        protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await execute(cancellationToken);
            }
            finally
            {
                TokenCancelled = cancellationToken.IsCancellationRequested;
            }
        }

        protected override ValueTask OnCancellingAsync(CancellationToken cancellationToken)
        {
            Cancelling = true;
            return ValueTask.CompletedTask;
        }
    }
}
