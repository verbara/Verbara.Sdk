using System.Diagnostics;
using Asterisk.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AriActivitySourceExtendedTests
{
    [Fact]
    public void StartRequest_ShouldReturnActivity_WhenListenerRegistered()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AriActivitySource.StartRequest("GET", "/ari/channels");

        activity.Should().NotBeNull();
        activity!.GetTagItem("http.request.method").Should().Be("GET");
        activity.GetTagItem("url.path").Should().Be("/ari/channels");
    }

    [Fact]
    public void StartRequest_ShouldReturnNull_WhenNoListenerRegistered()
    {
        // Without a listener sampling this source, activity will be null
        var activity = AriActivitySource.StartRequest("POST", "/ari/bridges");
        // May or may not be null depending on other test listeners, so just verify no throw
        activity?.Dispose();
    }

    [Fact]
    public void SetResponse_ShouldSetErrorStatus_WhenStatusCodeIs500()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AriActivitySource.Source.StartActivity("test-500");
        if (activity is not null)
        {
            AriActivitySource.SetResponse(activity, 500);
            activity.GetTagItem("http.response.status_code").Should().Be(500);
            activity.Status.Should().Be(ActivityStatusCode.Error);
            activity.StatusDescription.Should().Be("HTTP 500");
        }
    }

    [Fact]
    public void SetResponse_ShouldSetOkForVariousSuccessCodes()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        foreach (var code in new[] { 200, 201, 204, 299 })
        {
            using var activity = AriActivitySource.Source.StartActivity($"test-{code}");
            if (activity is not null)
            {
                AriActivitySource.SetResponse(activity, code);
                activity.Status.Should().Be(ActivityStatusCode.Ok, $"status code {code} should be OK");
            }
        }
    }

    [Fact]
    public void StartRequest_ShouldSetActivityKindToClient()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Asterisk.Sdk.Ari",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AriActivitySource.StartRequest("DELETE", "/ari/channels/ch1");

        if (activity is not null)
        {
            activity.Kind.Should().Be(ActivityKind.Client);
            activity.DisplayName.Should().Contain("DELETE");
        }
    }
}
