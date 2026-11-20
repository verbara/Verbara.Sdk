using System.Collections.Concurrent;
using System.Diagnostics;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Live.Tests.Diagnostics;

public sealed class LiveActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public LiveActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Verbara.Sdk.Live",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Source_ShouldHaveCorrectName()
    {
        LiveActivitySource.Source.Name.Should().Be("Verbara.Sdk.Live");
        LiveActivitySource.Source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void StartStateLoad_ShouldCreateActivity_WithServerTag()
    {
        using var activity = LiveActivitySource.StartStateLoad("asterisk-01", null);

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("live state-load");
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("live.server").Should().Be("asterisk-01");
    }

    [Fact]
    public void SetStateLoadResult_ShouldSetTags()
    {
        using var activity = LiveActivitySource.StartStateLoad("asterisk-01", null);

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
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200", null);

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
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200", null);

        LiveActivitySource.SetOriginateResult(activity, true, "Success");

        activity!.GetTagItem("originate.result").Should().Be("success");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetOriginateResult_ShouldSetErrorStatus_WhenFailure()
    {
        using var activity = LiveActivitySource.StartOriginate("SIP/100", "default", "200", null);

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

    // ---- server.id on Live's spans (live.server kept, obsolete) ----

    private static readonly ActivitySource ParentSource = new("Verbara.Sdk.Live.Tests.LiveActivitySource");

    [Fact]
    public void StartChannelReconcile_ShouldTagTheServerIdAndKeepLiveServer_WhenGivenAPoolId()
    {
        using var activity = LiveActivitySource.StartChannelReconcile("22.9.0", "n1");

        using (new AssertionScope())
        {
            activity!.GetTagItem("server.id").Should().Be("n1");
            activity.GetTagItem("live.server").Should().Be("22.9.0", "live.server keeps its value through 2.11.0");
            activity.GetTagItem("asterisk.server.version").Should().BeNull("the version stays a Resource attribute");
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldTagItsSpanWithThePoolIdAndTheAsteriskVersion_WhenTheServerIsInAPool()
    {
        using var spans = new ServerSpans();
        await using var run = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true });
        run.Server.PoolId = "n1";

        var span = await spans.TraceAsync("live channel-reconcile", () => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            span.GetTagItem("server.id").Should().Be("n1");
            span.GetTagItem("live.server").Should().Be(run.Server.AsteriskVersion, "live.server is unchanged: the version");
            span.GetTagItem("asterisk.server.version").Should().BeNull();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldTagItsLoadSpanWithNoServerId_WhenTheServerIsInNoPool()
    {
        using var spans = new ServerSpans();
        await using var run = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });

        var span = await spans.TraceAsync("live state-load", run.StartServerAsync);

        using (new AssertionScope())
        {
            span.GetTagItem("server.id").Should().BeNull("a standalone server has no pool id, and no empty or placeholder tag");
            span.GetTagItem("live.server").Should().Be(run.Server.AsteriskVersion);
            span.GetTagItem("asterisk.server.version").Should().BeNull();
        }
    }

    [Fact]
    public async Task ReconcileChannelsAsync_ShouldTagTheServerIdOnlyOnSpansStartedAfterTheServerJoinedAPool_WhenItJoinsAfterItsLoad()
    {
        using var spans = new ServerSpans();
        await using var run = await Run.ConnectAsync(new BootingAsterisk { BootedAtLogin = true });
        var load = await spans.TraceAsync("live state-load", run.StartServerAsync);
        run.Server.PoolId = "n1";

        var reconcile = await spans.TraceAsync("live channel-reconcile", () => run.Server.ReconcileChannelsAsync().AsTask());

        using (new AssertionScope())
        {
            load.GetTagItem("server.id").Should().BeNull("the load's span started before the server joined the pool");
            reconcile.GetTagItem("server.id").Should().Be("n1");
        }
    }

    [Fact]
    public async Task OriginateAsync_ShouldTagItsSpanWithThePoolIdAndNoLiveServer_WhenTheServerIsInAPool()
    {
        using var spans = new ServerSpans();
        await using var run = await Run.StartAsync(new BootingAsterisk { BootedAtLogin = true, OriginateOutcome = "Success" });
        run.Server.PoolId = "n1";

        var span = await spans.TraceAsync("live originate Local/s@hold",
            () => run.Server.OriginateAsync("Local/s@hold", "hold", "s").AsTask());

        using (new AssertionScope())
        {
            span.GetTagItem("server.id").Should().Be("n1");
            span.GetTagItem("live.server").Should().BeNull("live originate never carried live.server");
            span.GetTagItem("asterisk.server.version").Should().BeNull();
        }
    }

    /// <summary>Live's stopped spans, attributed by the parent activity each traced call runs in.</summary>
    private sealed class ServerSpans : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public ServerSpans()
        {
            var parentSource = ParentSource;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is "Verbara.Sdk.Live" || source == parentSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task<Activity> TraceAsync(string operationName, Func<Task> call)
        {
            string parentId;
            Task task;
            using (var parent = ParentSource.StartActivity("live-activity-source-test")
                ?? throw new InvalidOperationException("The test's parent activity was not sampled."))
            {
                parentId = parent.Id!;
                task = call();
            }

            await task.WaitAsync(Run.Bound);
            return _stopped.Single(a => a.ParentId == parentId && a.OperationName == operationName);
        }

        public void Dispose() => _listener.Dispose();
    }
}
