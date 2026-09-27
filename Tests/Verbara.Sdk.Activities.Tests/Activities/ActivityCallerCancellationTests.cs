using Verbara.Sdk;
using Verbara.Sdk.Activities.Activities;
using FluentAssertions;
using NSubstitute;

namespace Verbara.Sdk.Activities.Tests.Activities;

/// <summary>
/// Pins ADR-0022 for a cancellation that comes from the caller's token rather than from
/// <see cref="IActivity.CancelAsync"/>: the activity ends <see cref="ActivityStatus.Cancelled"/> and
/// <see cref="IActivity.StartAsync"/> returns without throwing.
/// </summary>
/// <remarks>
/// <para>
/// This held when the ADR's 2026-09-26 addendum measured it (arm B, with a throwaway console app), and no
/// test pinned it: every test that cancels calls <c>CancelAsync</c>, whose own write of <c>Cancelled</c>
/// hides whether the base's <c>catch (OperationCanceledException)</c> would have written it. Here
/// <c>CancelAsync</c> is never called, so the only writer of <c>Cancelled</c> is that catch; without it the
/// cancellation falls into the catch-all, which writes <c>Failed</c> and rethrows — the "failed with
/// OperationCanceledException" ending the ADR's Consequences name as the wrong result.
/// </para>
/// <para>
/// One case per base class, because the three carry the same catch chain as three copies
/// (<see cref="ActivityBase"/>, <see cref="AmiActivityBase"/>, <see cref="AriActivityBase"/>), and every
/// shipped activity inherits one of them. The activity parks on a source that only its token completes,
/// and signals once it is running, so the caller cancels a running activity by construction. The waits
/// are bounds that end on the signal they assert.
/// </para>
/// </remarks>
public sealed class ActivityCallerCancellationTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> ActivityBases => new()
    {
        nameof(ActivityBase),
        nameof(AmiActivityBase),
        nameof(AriActivityBase),
    };

    [Theory]
    [MemberData(nameof(ActivityBases))]
    public async Task StartAsync_ShouldEndCancelledWithoutThrowing_WhenTheCallerCancelsItsToken(string activityBase)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var activity = ParkedActivities.Create(activityBase, running);
        var statuses = new List<ActivityStatus>();
        using var subscription = activity.StatusChanges.Subscribe(statuses.Add);
        using var caller = new CancellationTokenSource();

        var start = activity.StartAsync(caller.Token).AsTask();
        await running.Task.WaitAsync(SignalTimeout);
        await caller.CancelAsync();

        var finishing = () => start.WaitAsync(SignalTimeout);

        await finishing.Should().NotThrowAsync(
            "ADR-0022: a cancellation reaches the observer as a status, not as an exception");
        activity.Status.Should().Be(ActivityStatus.Cancelled);
        statuses.Should().Equal(
            [ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Cancelled],
            "the caller's cancellation is the activity's last word, and nothing is written after it");
    }
}

/// <summary>
/// One activity per base class, each parked until its token is cancelled. <c>ExecuteAsync</c> signals
/// <c>running</c> first, so the test knows the activity is in progress before it cancels.
/// </summary>
file static class ParkedActivities
{
    public static IActivity Create(string activityBase, TaskCompletionSource running) => activityBase switch
    {
        nameof(ActivityBase) => new ParkedAgiActivity(Substitute.For<IAgiChannel>(), running),
        nameof(AmiActivityBase) => new ParkedAmiActivity(Substitute.For<IAmiConnection>(), running),
        nameof(AriActivityBase) => new ParkedAriActivity(Substitute.For<IAriClient>(), running),
        _ => throw new ArgumentOutOfRangeException(nameof(activityBase), activityBase, "no such activity base"),
    };

    public static async ValueTask ParkUntilCancelled(TaskCompletionSource running, CancellationToken cancellationToken)
    {
        running.TrySetResult();
        await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
    }
}

file sealed class ParkedAgiActivity(IAgiChannel channel, TaskCompletionSource running) : ActivityBase(channel)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) =>
        ParkedActivities.ParkUntilCancelled(running, cancellationToken);
}

file sealed class ParkedAmiActivity(IAmiConnection ami, TaskCompletionSource running) : AmiActivityBase(ami)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) =>
        ParkedActivities.ParkUntilCancelled(running, cancellationToken);
}

file sealed class ParkedAriActivity(IAriClient ariClient, TaskCompletionSource running) : AriActivityBase(ariClient)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) =>
        ParkedActivities.ParkUntilCancelled(running, cancellationToken);
}
