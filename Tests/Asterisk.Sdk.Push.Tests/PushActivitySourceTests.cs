using System.Diagnostics;

namespace Asterisk.Sdk.Push.Tests;

public sealed class PushActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public PushActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Asterisk.Sdk.Push",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        PushActivitySource.Source.Name.Should().Be("Asterisk.Sdk.Push");
        PushActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void StartPublish_ShouldCreateActivity_WithEventTypeTag()
    {
        using var activity = PushActivitySource.StartPublish("conversation.message");

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("push publish conversation.message");
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("push.event_type").Should().Be("conversation.message");
    }

    [Fact]
    public void SetPublished_ShouldSetOkStatus()
    {
        using var activity = PushActivitySource.StartPublish("test.event");

        PushActivitySource.SetPublished(activity);

        activity!.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetPublished_ShouldBeNoOp_WhenActivityIsNull()
    {
        var act = () => PushActivitySource.SetPublished(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void StartDelivery_ShouldCreateActivity_WithSubscriberCount()
    {
        using var activity = PushActivitySource.StartDelivery("agent.status", 5);

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("push deliver agent.status");
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.GetTagItem("push.event_type").Should().Be("agent.status");
        activity.GetTagItem("push.subscriber_count").Should().Be(5);
    }

    [Fact]
    public void SetDeliveryResult_ShouldSetOkStatus_WhenNoDrops()
    {
        using var activity = PushActivitySource.StartDelivery("test.event", 3);

        PushActivitySource.SetDeliveryResult(activity, delivered: 3, dropped: 0);

        activity!.GetTagItem("push.delivered").Should().Be(3);
        activity.GetTagItem("push.dropped").Should().Be(0);
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetDeliveryResult_ShouldSetErrorStatus_WhenDropped()
    {
        using var activity = PushActivitySource.StartDelivery("test.event", 4);

        PushActivitySource.SetDeliveryResult(activity, delivered: 2, dropped: 2);

        activity!.GetTagItem("push.delivered").Should().Be(2);
        activity.GetTagItem("push.dropped").Should().Be(2);
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("2 subscriber(s) failed");
    }

    [Fact]
    public void SetDeliveryResult_ShouldBeNoOp_WhenActivityIsNull()
    {
        var act = () => PushActivitySource.SetDeliveryResult(null, 1, 0);
        act.Should().NotThrow();
    }
}
