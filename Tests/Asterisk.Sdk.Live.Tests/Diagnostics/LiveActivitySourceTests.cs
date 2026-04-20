using System.Diagnostics;
using Asterisk.Sdk.Live.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Live.Tests.Diagnostics;

public sealed class LiveActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public LiveActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Asterisk.Sdk.Live",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        LiveActivitySource.Source.Name.Should().Be("Asterisk.Sdk.Live");
        LiveActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void StartStateLoad_ShouldCreateActivity_WithServerTag()
    {
        using var activity = LiveActivitySource.StartStateLoad("asterisk-01");

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("live state-load");
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("live.server").Should().Be("asterisk-01");
    }

    [Fact]
    public void SetStateLoadResult_ShouldSetTags()
    {
        using var activity = LiveActivitySource.StartStateLoad("asterisk-01");

        LiveActivitySource.SetStateLoadResult(activity, 5, 3, 12);

        activity!.GetTagItem("live.channels").Should().Be(5);
        activity.GetTagItem("live.queues").Should().Be(3);
        activity.GetTagItem("live.agents").Should().Be(12);
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetStateLoadResult_ShouldBeNoOp_WhenActivityIsNull()
    {
        var act = () => LiveActivitySource.SetStateLoadResult(null, 1, 2, 3);
        act.Should().NotThrow();
    }

    [Fact]
    public void StartOriginate_ShouldCreateActivity_WithChannelTags()
    {
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200");

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("live originate SIP/100");
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("asterisk.channel.name").Should().Be("SIP/100");
        activity.GetTagItem("dialplan.context").Should().Be("default");
        activity.GetTagItem("dialplan.extension").Should().Be("200");
    }

    [Fact]
    public void SetOriginateResult_ShouldSetOkStatus_WhenSuccess()
    {
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200");

        LiveActivitySource.SetOriginateResult(activity, true, "Success");

        activity!.GetTagItem("originate.result").Should().Be("success");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetOriginateResult_ShouldSetErrorStatus_WhenFailure()
    {
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200");

        LiveActivitySource.SetOriginateResult(activity, false, "No OriginateResponse received");

        activity!.GetTagItem("originate.result").Should().Be("failure");
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("No OriginateResponse received");
    }

    [Fact]
    public void SetOriginateResult_ShouldBeNoOp_WhenActivityIsNull()
    {
        var act = () => LiveActivitySource.SetOriginateResult(null, true, "msg");
        act.Should().NotThrow();
    }
}
