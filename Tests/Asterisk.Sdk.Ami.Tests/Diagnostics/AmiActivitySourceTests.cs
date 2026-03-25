using System.Diagnostics;
using Asterisk.Sdk.Ami.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Diagnostics;

public sealed class AmiActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public AmiActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Asterisk.Sdk.Ami",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        AmiActivitySource.Source.Name.Should().Be("Asterisk.Sdk.Ami");
        AmiActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void StartAction_ShouldCreateActivity_WithCorrectTags()
    {
        using var activity = AmiActivitySource.StartAction("Status", "action-123");

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("ami Status");
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("ami.action").Should().Be("Status");
        activity.GetTagItem("ami.action_id").Should().Be("action-123");
    }

    [Fact]
    public void SetResponse_ShouldSetSuccessTags_WhenResponseIsSuccess()
    {
        using var activity = AmiActivitySource.StartAction("Originate", "act-1");

        AmiActivitySource.SetResponse(activity, "Success", "Originate successfully queued");

        activity!.GetTagItem("ami.response").Should().Be("Success");
        activity.GetTagItem("ami.message").Should().Be("Originate successfully queued");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetResponse_ShouldSetErrorStatus_WhenResponseIsError()
    {
        using var activity = AmiActivitySource.StartAction("Hangup", "act-2");

        AmiActivitySource.SetResponse(activity, "Error", "No such channel");

        activity!.GetTagItem("ami.response").Should().Be("Error");
        activity.GetTagItem("ami.message").Should().Be("No such channel");
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("No such channel");
    }

    [Fact]
    public void SetResponse_ShouldNotSetMessageTag_WhenMessageIsNull()
    {
        using var activity = AmiActivitySource.StartAction("Ping", "act-3");

        AmiActivitySource.SetResponse(activity, "Success", null);

        activity!.GetTagItem("ami.response").Should().Be("Success");
        activity.GetTagItem("ami.message").Should().BeNull();
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetResponse_ShouldBeNoOp_WhenActivityIsNull()
    {
        // Should not throw
        var act = () => AmiActivitySource.SetResponse(null, "Success", "msg");
        act.Should().NotThrow();
    }

    [Fact]
    public void SetResponse_ShouldBeCaseInsensitive_ForErrorDetection()
    {
        using var activity = AmiActivitySource.StartAction("Command", "act-4");

        AmiActivitySource.SetResponse(activity, "error", "Permission denied");

        activity!.Status.Should().Be(ActivityStatusCode.Error);
    }
}
