using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

using FluentAssertions.Execution;

namespace Verbara.Sdk.Push.Tests;

/// <summary>
/// Every test class that adds a global <see cref="ActivityListener"/> on the push source
/// (<c>Verbara.Sdk.Push</c>, a fixed static source, so a per-class source name cannot isolate
/// <c>push deliver</c>) runs in this collection, never alongside another one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PushTracingGroup
{
    public const string Name = "Push tracing";
}

/// <summary>
/// Spec <c>push-delivery-tracing</c>, in-process half: the publisher's trace context is captured at publish,
/// the <c>push deliver</c> span is its child, the dispatch loop does not inherit the activity of whoever
/// built the bus, and without a usable context the delivery is a root.
/// </summary>
[Collection(PushTracingGroup.Name)]
public sealed class PushDeliveryTracingTests : IDisposable
{
    private const int N = 30;
    private const string ExplicitTraceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ActivitySource Api = new("Verbara.Sdk.Push.Tests.Tracing");

    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _started = new();

    public PushDeliveryTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Verbara.Sdk.Push" or "Verbara.Sdk.Push.Tests.Tracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => _started.Enqueue(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task PublishAsync_ShouldPreserveTraceContext_WhenActivityIsAmbient()
    {
        using var bus = NewBus();
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        using var handler = Api.StartActivity("api.handler");
        handler.Should().NotBeNull("the test listener samples its own source");
        await bus.PublishAsync(TestEventFactory.Create(payload: "adr0019"));
        await rec.WaitForAsync(1, Bound);

        rec.Seen["adr0019"].TraceContext.Should().Be(handler!.Id,
            "the bus records the publisher's ambient traceparent when the publisher left TraceContext empty");
    }

    [Fact]
    public async Task PublishAsync_ShouldKeepExplicitTraceContext_WhenAnotherActivityIsAmbient()
    {
        using var bus = NewBus();
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        using (var handler = Api.StartActivity("api.handler"))
        {
            handler.Should().NotBeNull();
            await bus.PublishAsync(Event("explicit", ExplicitTraceparent));
        }
        await rec.WaitForAsync(1, Bound);

        rec.Seen["explicit"].TraceContext.Should().Be(ExplicitTraceparent,
            "a TraceContext the publisher set explicitly is kept unchanged");
    }

    [Fact]
    public async Task DispatchLoop_ShouldParentDeliveryOnPublisher_WhenActivityIsAmbient()
    {
        using var bus = NewBus();
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        var handlers = await PublishUnderHandlersAsync(bus, "s1");
        await rec.WaitForAsync(N, Bound);

        var delivers = handlers.Select(h => (Handler: h.Value, Deliver: rec.Seen[h.Key].Ambient)).ToArray();
        using var scope = new AssertionScope();
        delivers.Count(d => d.Deliver is { } a && a.OperationName.StartsWith("push deliver", StringComparison.Ordinal)
                && a.Source.Name == "Verbara.Sdk.Push")
            .Should().Be(N, "the subscriber's ambient activity during delivery is the push deliver span");
        delivers.Count(d => d.Deliver?.ParentSpanId == d.Handler.SpanId && d.Deliver.TraceId == d.Handler.TraceId)
            .Should().Be(N, "each push deliver span has its own api.handler as parent (30 of 30)");
    }

    [Fact]
    public async Task DispatchLoop_ShouldNotInheritConstructorActivity_WhenBusIsBuiltInsideAnActivity()
    {
        RxPushEventBus bus;
        Activity? startup;
        using (startup = Api.StartActivity("host.startup"))
        {
            startup.Should().NotBeNull();
            bus = NewBus();
        }
        using var _ = bus;
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        var handlers = await PublishUnderHandlersAsync(bus, "s2");
        await rec.WaitForAsync(N, Bound);

        var delivers = handlers.Select(h => (Handler: h.Value, Deliver: rec.Seen[h.Key].Ambient)).ToArray();
        using var scope = new AssertionScope();
        delivers.Count(d => d.Deliver is { } a && a.ParentSpanId == startup!.SpanId)
            .Should().Be(0, "no push deliver span has host.startup as parent (0 of 30)");
        delivers.Count(d => d.Deliver?.ParentSpanId == d.Handler.SpanId && d.Deliver.TraceId == d.Handler.TraceId)
            .Should().Be(N, "each push deliver span is a child of its own api.handler (30 of 30)");
    }

    [Fact]
    public async Task DispatchLoop_ShouldStartRootDelivery_WhenNoActivityIsAmbient()
    {
        using var bus = NewBus();
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        Activity.Current.Should().BeNull("the scenario publishes with no activity current");
        for (var i = 0; i < N; i++)
            await bus.PublishAsync(TestEventFactory.Create(payload: Key("s3", i)));
        await rec.WaitForAsync(N, Bound);

        var delivers = rec.Seen.Values.Select(v => v.Ambient).ToArray();
        using var scope = new AssertionScope();
        delivers.Count(a => a is not null && a.OperationName.StartsWith("push deliver", StringComparison.Ordinal))
            .Should().Be(N, "every delivery still starts a push deliver span");
        delivers.Count(a => a is not null && a.ParentSpanId == default && a.Parent is null)
            .Should().Be(N, "with nothing ambient at publish every push deliver span is a root (30 of 30)");
        delivers.Where(a => a is not null).Select(a => a!.TraceId).Distinct().Count()
            .Should().Be(N, "the 30 root deliveries are in 30 distinct traces");
    }

    [Fact]
    public async Task DispatchLoop_ShouldStartRootDelivery_WhenTraceContextIsMalformed()
    {
        using var bus = NewBus();
        var rec = new DeliveryRecorder();
        using var sub = bus.AsObservable().Subscribe(rec);

        await bus.PublishAsync(Event("malformed", "not-a-traceparent"));
        await rec.WaitForAsync(1, Bound);

        var seen = rec.Seen["malformed"];
        seen.TraceContext.Should().Be("not-a-traceparent", "the subscriber receives the event as published");
        seen.Ambient.Should().NotBeNull("the delivery still starts a push deliver span");
        seen.Ambient!.OperationName.Should().StartWith("push deliver");
        seen.Ambient.ParentSpanId.Should().Be(default(ActivitySpanId),
            "a TraceContext that does not parse as W3C leaves push deliver a root");
    }

    private static async Task<Dictionary<string, Activity>> PublishUnderHandlersAsync(RxPushEventBus bus, string scenario)
    {
        var handlers = new Dictionary<string, Activity>(StringComparer.Ordinal);
        for (var i = 0; i < N; i++)
        {
            var key = Key(scenario, i);
            var handler = Api.StartActivity("api.handler");
            handler.Should().NotBeNull();
            await bus.PublishAsync(TestEventFactory.Create(payload: key));
            handler!.Stop();
            handlers[key] = handler;
        }
        return handlers;
    }

    private static string Key(string scenario, int i) => scenario + "-" + i.ToString(CultureInfo.InvariantCulture);

    private static TestPushEvent Event(string payload, string traceContext) => new()
    {
        Payload = payload,
        Metadata = new PushEventMetadata("tenant-1", null, DateTimeOffset.UtcNow, null) { TraceContext = traceContext },
    };

    // BufferCapacity above every event a test publishes, so no event is evicted (design D7).
    private static RxPushEventBus NewBus() => new(
        Options.Create(new PushEventBusOptions { BufferCapacity = 1024 }),
        NullLogger<RxPushEventBus>.Instance,
        new PushMetrics());

    /// <summary>Records, per payload, the subscriber's ambient activity and the event's TraceContext.</summary>
    private sealed class DeliveryRecorder : IObserver<PushEvent>
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

        public ConcurrentDictionary<string, (Activity? Ambient, string? TraceContext)> Seen { get; } = new(StringComparer.Ordinal);

        public void OnNext(PushEvent value)
        {
            Seen[((TestPushEvent)value).Payload] = (Activity.Current, value.Metadata.TraceContext);
            lock (_gate)
                _waiters.RemoveAll(w => Seen.Count >= w.Count && w.Signal.TrySetResult());
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public async Task WaitForAsync(int count, TimeSpan bound)
        {
            Task signal;
            lock (_gate)
            {
                if (Seen.Count >= count) return;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, tcs));
                signal = tcs.Task;
            }
            await signal.WaitAsync(bound);
        }
    }
}
