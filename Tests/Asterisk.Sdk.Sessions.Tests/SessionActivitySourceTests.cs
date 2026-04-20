using System.Diagnostics;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class SessionActivitySourceTests : IDisposable
{
    private readonly List<Activity> _collected = [];
    private readonly ActivityListener _listener;

    public SessionActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Asterisk.Sdk.Sessions",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _collected.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void StartSessionCompleted_ShouldCreateActivity_WithCorrectTags()
    {
        using var activity = SessionActivitySource.StartSessionCompleted(
            "abc123", CallDirection.Inbound, CallSessionState.Completed, TimeSpan.FromSeconds(42));

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Contain("abc123");
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.GetTagItem("session.id").Should().Be("abc123");
        activity.GetTagItem("call.direction").Should().Be("inbound");
        activity.GetTagItem("call.state").Should().Be("completed");
        activity.GetTagItem("call.duration_ms").Should().Be(42000.0);
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void StartSessionCompleted_ShouldSetErrorStatus_WhenFailed()
    {
        using var activity = SessionActivitySource.StartSessionCompleted(
            "fail1", CallDirection.Outbound, CallSessionState.Failed, TimeSpan.FromSeconds(5));

        activity.Should().NotBeNull();
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("Failed");
        activity.GetTagItem("call.direction").Should().Be("outbound");
    }

    [Fact]
    public void StartSessionCompleted_ShouldSetErrorStatus_WhenTimedOut()
    {
        using var activity = SessionActivitySource.StartSessionCompleted(
            "to1", CallDirection.Inbound, CallSessionState.TimedOut, TimeSpan.FromSeconds(30));

        activity.Should().NotBeNull();
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("TimedOut");
    }

    [Fact]
    public void StartReconciliation_ShouldCreateActivity()
    {
        using var activity = SessionActivitySource.StartReconciliation();

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("session reconciliation");
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        SessionActivitySource.Source.Name.Should().Be("Asterisk.Sdk.Sessions");
        SessionActivitySource.Source.Version.Should().Be("1.0.0");
    }
}
