using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Nats;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace Asterisk.Sdk.Push.Nats.IntegrationTests;

/// <summary>
/// Bidirectional bridge against a real NATS server (Testcontainers). Two hosts — node A
/// and node B — each run their own <see cref="NatsBridge"/>, both subscribing to the
/// same subject prefix. Verifies cross-node delivery + that self-origin messages are
/// correctly skipped so the loop prevention guard holds.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Nats")]
public sealed class NatsBridgeBidirectionalTests(NatsContainerFixture fixture)
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Bidirectional_ShouldDeliverToPeer_AndSkipSelfOrigin()
    {
        const string prefix = "asterisk.sdk.bidir";
        const string nodeA = "nodeA";
        const string nodeB = "nodeB";

        using var hostA = BuildHost(nodeA, prefix);
        using var hostB = BuildHost(nodeB, prefix);

        await hostA.StartAsync();
        await hostB.StartAsync();

        // Give both subscribe loops time to establish NATS interest before we publish.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var busA = hostA.Services.GetRequiredService<IPushEventBus>();
        var busB = hostB.Services.GetRequiredService<IPushEventBus>();

        var remoteSignal = new TaskCompletionSource<RemotePushEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = busB.OfType<RemotePushEvent>()
            .Subscribe(new SingleShotObserver<RemotePushEvent>(remoteSignal));

        // Also subscribe on node A to detect whether its own message loops back.
        var selfReceived = 0;
        using var selfSubscription = busA.OfType<RemotePushEvent>()
            .Subscribe(new CountingObserver<RemotePushEvent>(() => Interlocked.Increment(ref selfReceived)));

        var published = new BidirectionalTestEvent
        {
            Metadata = new PushEventMetadata(
                TenantId: "tenant-bidir",
                UserId: "user-bidir",
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: Guid.NewGuid().ToString("N"),
                TopicPath: "bidir.hello"),
        };
        await busA.PublishAsync(published);

        var seen = await remoteSignal.Task.WaitAsync(ReceiveTimeout);
        seen.OriginalEventType.Should().Be("bidir.integration.test");
        seen.SourceNodeId.Should().Be(nodeA);

        // Loop-prevention: node A must not see the event it published re-emerge as a
        // RemotePushEvent — its subscribe loop should have dropped it as self-originated.
        // Allow a small grace window for any in-flight delivery.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Volatile.Read(ref selfReceived).Should().Be(0);

        await hostA.StopAsync();
        await hostB.StopAsync();
    }

    [Fact]
    public async Task Bidirectional_ShouldNotLoopOnNodeA_WhenSkipSelfOriginatedIsOn()
    {
        const string prefix = "asterisk.sdk.noloop";
        const string nodeA = "nodeA";

        using var hostA = BuildHost(nodeA, prefix);
        await hostA.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var busA = hostA.Services.GetRequiredService<IPushEventBus>();

        var remoteCount = 0;
        using var subscription = busA.OfType<RemotePushEvent>()
            .Subscribe(new CountingObserver<RemotePushEvent>(() => Interlocked.Increment(ref remoteCount)));

        for (var i = 0; i < 5; i++)
        {
            var evt = new BidirectionalTestEvent
            {
                Metadata = new PushEventMetadata(
                    TenantId: "tenant-noloop",
                    UserId: null,
                    OccurredAt: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    TopicPath: $"noloop.{i}"),
            };
            await busA.PublishAsync(evt);
        }

        // Wait long enough for NATS round trip — if the skip-self guard were broken the
        // counter would grow during this window.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Volatile.Read(ref remoteCount).Should().Be(0);

        await hostA.StopAsync();
    }

    private IHost BuildHost(string nodeId, string prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAsteriskPush();
        builder.Services.AddPushNats(opt =>
        {
            opt.Url = fixture.Url;
            opt.SubjectPrefix = prefix;
            opt.ConnectTimeoutSeconds = 5;
            opt.NodeId = nodeId;
            opt.Subscribe = new NatsSubscribeOptions
            {
                SubjectFilters = [$"{prefix}.>"],
                SkipSelfOriginated = true,
            };
        });
        return builder.Build();
    }

    private sealed record BidirectionalTestEvent : PushEvent
    {
        public override string EventType => "bidir.integration.test";
    }

    private sealed class SingleShotObserver<T>(TaskCompletionSource<T> tcs) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => tcs.TrySetException(error);
        public void OnNext(T value) => tcs.TrySetResult(value);
    }

    private sealed class CountingObserver<T>(Action onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext();
    }
}
