using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The sweep switched off by an infinite interval, the intervals that still fail its start, a connection that does
/// not report how an action ended, and what one sweep reports on its span.
/// </summary>
[Collection(SweepCounterGroup.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SweepSwitchesAndReportsTests : IAsyncLifetime
{
    private readonly List<SweepRig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
            await rig.DisposeAsync().AsTask().WaitAsync(SweepRig.Bound);
    }

    private SweepRig Rig(bool reportsOutcome = true)
    {
        var rig = new SweepRig(reportsOutcome: reportsOutcome);
        _rigs.Add(rig);
        return rig;
    }

    [Fact]
    public async Task StartAsync_ShouldStartNoTimerNorLoopAndSendNothing_WhenTheIntervalIsInfinite()
    {
        var rig = Rig();
        var old = rig.DialingCall("old");
        rig.Age(old);
        var before = SweepRig.Look(old);
        rig.Options.ReconciliationInterval = Timeout.InfiniteTimeSpan;
        var sweep = rig.BuildSweep();

        var startError = await Record.ExceptionAsync(() => sweep.StartAsync(CancellationToken.None));
        var started = new { Timer = Field(sweep, "_timer"), Loop = Field(sweep, "_runningTask") };
        var stopError = await Record.ExceptionAsync(() => sweep.StopAsync(CancellationToken.None).WaitAsync(SweepRig.Bound));

        new
        {
            StartThrew = startError is not null,
            StopThrew = stopError is not null,
            TimerStarted = started.Timer is not null,
            LoopStarted = started.Loop is not null,
            Actions = rig.ActionsSent().Count,
            Call = SweepRig.Look(old),
        }.Should().BeEquivalentTo(
            new { StartThrew = false, StopThrew = false, TimerStarted = false, LoopStarted = false, Actions = 0, Call = before },
            $"an infinite interval switches the sweep off: no timer, no loop, nothing sent, and the host starts and stops "
            + $"as usual. Start: {startError?.GetType().Name ?? "none"}, stop: {stopError?.GetType().Name ?? "none"}. "
            + $"Measured: {rig.Describe()}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2000)]
    public async Task StartAsync_ShouldThrow_WhenTheIntervalIsZeroOrNegativeAndNotInfinite(int milliseconds)
    {
        var rig = Rig();
        rig.Options.ReconciliationInterval = TimeSpan.FromMilliseconds(milliseconds);
        var sweep = rig.BuildSweep();

        var error = await Record.ExceptionAsync(() => sweep.StartAsync(CancellationToken.None));

        error.Should().BeOfType<ArgumentOutOfRangeException>(
            "only the infinite interval switches the sweep off; any other interval of zero or less fails the start");
    }

    [Fact]
    public async Task Sweep_ShouldSendNoStatusAndEndNothing_WhenTheConnectionDoesNotReportHowAnActionEnded()
    {
        var rig = Rig(reportsOutcome: false);
        var dialing = rig.DialingCall("dialing");
        rig.Age(dialing);
        var created = rig.CreatedCall("created");
        rig.Age(created);
        rig.AsteriskLists("dialing");
        var before = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) };
        var sweep = rig.BuildSweep();

        await SweepRig.SweepOnceAsync(sweep);

        new
        {
            Status = rig.Sent<StatusAction>(),
            CallEndedEvents = rig.Endings.Count,
            Calls = new { Dialing = SweepRig.Look(dialing), Created = SweepRig.Look(created) },
        }.Should().BeEquivalentTo(
            new { Status = 0, CallEndedEvents = 0, Calls = before },
            "a connection that does not report how an action ended cannot tell a refused Status from an empty one, so "
            + $"the sweep does not verify over it: an empty answer would end every candidate. Measured: {rig.Describe()}");
    }

    [Fact]
    public async Task Sweep_ShouldTagItsSpanWithWhatItFoundAndDid_WhenItVerifies()
    {
        var rig = Rig();
        rig.Age(rig.DialingCall("listed"));
        var dropped = rig.DialingCall("dropped");
        rig.Age(dropped);
        rig.Age(rig.ZeroChannelSession("unheld"));
        rig.DialingCall("young");
        rig.AsteriskLists("listed", "young");
        var sweep = rig.BuildSweep();

        var verified = await SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));
        rig.ConnectionState = AmiConnectionState.Reconnecting;
        var skipped = await SweepTagsAsync(() => SweepRig.SweepOnceAsync(sweep));

        new { Verified = verified, Skipped = skipped, Dropped = dropped.State }.Should().BeEquivalentTo(
            new
            {
                Verified = new Dictionary<string, object?>
                {
                    ["sessions.candidates"] = 2,
                    ["sessions.unverifiable"] = 1,
                    ["verification"] = "run",
                    ["sessions.ended"] = 1,
                },
                Skipped = new Dictionary<string, object?>
                {
                    ["sessions.candidates"] = 1,
                    ["sessions.unverifiable"] = 1,
                    ["verification"] = "skipped:not-connected",
                    ["sessions.ended"] = 0,
                },
                Dropped = CallSessionState.Failed,
            },
            "a sweep tags its span with the calls it would verify, the ones no snapshot can prove gone, whether it "
            + $"verified or why not, and how many of its candidates ended. Measured: {rig.Describe()}");
    }

    /// <summary>The tags of the one <c>session reconciliation</c> span <paramref name="sweep"/> opens.</summary>
    private static async Task<Dictionary<string, object?>> SweepTagsAsync(Func<Task> sweep)
    {
        var spans = new ConcurrentQueue<Activity>();
        var previous = Activity.Current;
        using var parent = new Activity("sweep-tags-under-test");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Verbara.Sdk.Sessions",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "session reconciliation" && activity.TraceId == parent.TraceId)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        parent.Start();
        try
        {
            await sweep().WaitAsync(SweepRig.Bound);
        }
        finally
        {
            parent.Stop();
            Activity.Current = previous;
        }

        spans.Should().ContainSingle("one sweep opens one reconciliation span");
        return spans.Single().TagObjects
            .Where(tag => tag.Key is "sessions.candidates" or "sessions.unverifiable" or "verification" or "sessions.ended")
            .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
    }

    private static object? Field(SessionReconciliationService sweep, string name) =>
        (typeof(SessionReconciliationService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"SessionReconciliationService has no field {name}."))
        .GetValue(sweep);
}
