using Verbara.Sdk;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Models;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;

namespace Verbara.Sdk.Activities.Tests.Activities;

/// <summary>
/// How an activity chooses its ending: <see cref="ActivityStatus.Cancelled"/> when someone asked it to stop
/// (the caller's token, <see cref="IActivity.CancelAsync"/>, disposal), <see cref="ActivityStatus.Failed"/>
/// for everything else — a deadline the caller did not set included — with <see cref="IActivity.StartAsync"/>
/// rethrowing only the latter.
/// </summary>
/// <remarks>
/// <para>
/// One case per base class, because <see cref="ActivityBase"/>, <see cref="AmiActivityBase"/> and
/// <see cref="AriActivityBase"/> carry the same ending logic as three copies, and every shipped activity
/// inherits one of them; a copy that drifts goes red on its own row.
/// </para>
/// <para>
/// Every activity here signals <c>running</c> before the test triggers its ending, so the order is fixed by
/// construction; the waits are bounds that end on the signal they assert. Every park awaits <b>without</b>
/// <c>ConfigureAwait(false)</c>, and every trigger cancels synchronously: the unwind then runs inside the
/// cancelling call, the path a console app or a generic host takes. A park that posts its continuation
/// elsewhere would read a request flag set too late as if it were set in time.
/// </para>
/// </remarks>
public sealed class ActivityEndingTests
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
    public async Task StartAsync_ShouldEndFailedAndThrow_WhenADeadlineTheCallerDidNotSetEndsIt(string activityBase)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource();
        await using var activity = EndingActivities.Create(activityBase, async _ =>
        {
            running.TrySetResult();
            // The deadline is the activity's own: neither the caller's token nor CancelAsync reaches it.
            await new TaskCompletionSource().Task.WaitAsync(deadline.Token);
        });
        var statuses = new List<ActivityStatus>();
        using var subscription = activity.StatusChanges.Subscribe(statuses.Add);
        using var caller = new CancellationTokenSource();

        var start = activity.StartAsync(caller.Token).AsTask();
        await running.Task.WaitAsync(SignalTimeout);
        EndingActivities.CancelInline(deadline);

        var finishing = () => start.WaitAsync(SignalTimeout);

        using var scope = new AssertionScope();
        await finishing.Should().ThrowAsync<OperationCanceledException>(
            "a deadline nobody asked for is a failure, and StartAsync rethrows it unchanged");
        caller.IsCancellationRequested.Should().BeFalse("the caller never asked the activity to stop");
        activity.Status.Should().Be(ActivityStatus.Failed);
        statuses.Should().Equal(
            [ActivityStatus.Pending, ActivityStatus.Starting, ActivityStatus.InProgress, ActivityStatus.Failed],
            "an ending nobody requested is Failed, never Cancelled");
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldEndFailed_WhenTheAmiResponseTimesOut()
    {
        // The shape an unanswered action takes: the connection's response wait is linked to its own
        // response timeout, and that timeout surfaces as a cancellation carrying the linked token.
        using var responseTimeout = new CancellationTokenSource();
        await responseTimeout.CancelAsync();
        var ami = Substitute.For<IAmiConnection>();
#pragma warning disable CA2012 // NSubstitute setup: the ValueTask is returned to the activity, which awaits it once.
        ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ManagerResponse>(Task.FromException<ManagerResponse>(
                new TaskCanceledException("The AMI response did not arrive in time.", null, responseTimeout.Token))));
#pragma warning restore CA2012
        await using var activity = new AttendedTransferActivity(ami)
        {
            Channel = "PJSIP/alice-00001",
            Destination = new DialPlanExtension("internal", "2001"),
        };

        var starting = () => activity.StartAsync().AsTask().WaitAsync(SignalTimeout);

        using var scope = new AssertionScope();
        await starting.Should().ThrowAsync<OperationCanceledException>(
            "a response timeout is the connection's deadline, not the caller's request");
        activity.Status.Should().Be(ActivityStatus.Failed);
    }

    [Fact]
    public async Task SnoopActivity_ShouldEndFailed_WhenTheAriRequestTimesOut()
    {
        var ari = Substitute.For<IAriClient>();
        var channels = Substitute.For<IAriChannelsResource>();
        ari.Channels.Returns(channels);
#pragma warning disable CA2012 // NSubstitute setup: the ValueTask is returned to the activity, which awaits it once.
        // The shape an HTTP client timeout takes: a TaskCanceledException whose inner exception is a TimeoutException.
        channels.SnoopAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AriChannel>(Task.FromException<AriChannel>(
                new TaskCanceledException("The ARI request timed out.", new TimeoutException()))));
#pragma warning restore CA2012
        await using var activity = new SnoopActivity(ari) { TargetChannelId = "target-001", App = "supervision" };

        var starting = () => activity.StartAsync().AsTask().WaitAsync(SignalTimeout);

        using var scope = new AssertionScope();
        await starting.Should().ThrowAsync<OperationCanceledException>(
            "an HTTP client timeout is the transport's deadline, not the caller's request");
        activity.Status.Should().Be(ActivityStatus.Failed);
        activity.SnoopChannel.Should().BeNull("the request that would have created it never completed");
    }
}

/// <summary>
/// One activity per base class, each running the body the test hands it as its <c>ExecuteAsync</c>.
/// </summary>
file static class EndingActivities
{
    public static IActivity Create(string activityBase, Func<CancellationToken, ValueTask> execute) => activityBase switch
    {
        nameof(ActivityBase) => new EndingAgiActivity(Substitute.For<IAgiChannel>(), execute),
        nameof(AmiActivityBase) => new EndingAmiActivity(Substitute.For<IAmiConnection>(), execute),
        nameof(AriActivityBase) => new EndingAriActivity(Substitute.For<IAriClient>(), execute),
        _ => throw new ArgumentOutOfRangeException(nameof(activityBase), activityBase, "no such activity base"),
    };

    /// <summary>
    /// Cancels on the calling thread, so registered continuations — the parked execution's unwind among
    /// them — run inside this call, as they do under a host with no synchronization context.
    /// <c>CancellationTokenSource.CancelAsync</c> would run them on the thread pool instead.
    /// </summary>
    public static void CancelInline(CancellationTokenSource source) => source.Cancel();
}

file sealed class EndingAgiActivity(IAgiChannel channel, Func<CancellationToken, ValueTask> execute) : ActivityBase(channel)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) => execute(cancellationToken);
}

file sealed class EndingAmiActivity(IAmiConnection ami, Func<CancellationToken, ValueTask> execute) : AmiActivityBase(ami)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) => execute(cancellationToken);
}

file sealed class EndingAriActivity(IAriClient ariClient, Func<CancellationToken, ValueTask> execute) : AriActivityBase(ariClient)
{
    protected override ValueTask ExecuteAsync(CancellationToken cancellationToken) => execute(cancellationToken);
}
