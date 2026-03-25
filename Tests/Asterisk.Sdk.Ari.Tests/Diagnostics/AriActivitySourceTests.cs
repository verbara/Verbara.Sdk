using System.Diagnostics;
using Asterisk.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AriActivitySourceTests
{
    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        AriActivitySource.Source.Name.Should().Be("Asterisk.Sdk.Ari");
        AriActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void SetResponse_ShouldHandleNullActivity()
    {
        // Should not throw when activity is null
        var act = () => AriActivitySource.SetResponse(null, 200);
        act.Should().NotThrow();
    }

    [Fact]
    public void SetResponse_ShouldSetOkStatus_WhenStatusCodeIsSuccess()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AriActivitySource.Source.StartActivity("test");
        if (activity is not null)
        {
            AriActivitySource.SetResponse(activity, 200);
            activity.GetTagItem("http.response.status_code").Should().Be(200);
            activity.Status.Should().Be(ActivityStatusCode.Ok);
        }
    }

    [Fact]
    public void SetResponse_ShouldSetErrorStatus_WhenStatusCodeIs4xx()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AriActivitySource.Source.StartActivity("test");
        if (activity is not null)
        {
            AriActivitySource.SetResponse(activity, 404);
            activity.Status.Should().Be(ActivityStatusCode.Error);
        }
    }
}
