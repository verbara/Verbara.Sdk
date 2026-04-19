using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Nats;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Xunit;

namespace Asterisk.Sdk.Push.Nats.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("Nats")]
public sealed class NatsBridgeIntegrationTests(NatsContainerFixture fixture)
{
    private static readonly TimeSpan SubscriptionWarmup = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Bridge_ShouldPublishToNats_WhenPushEventOccurs()
    {
        using var host = BuildHost(prefix: "asterisk.sdk");
        await host.StartAsync();

        await using var client = await fixture.CreateClientAsync();
        var received = new TaskCompletionSource<NatsMsg<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var msg in client.SubscribeAsync<byte[]>("asterisk.sdk.calls.>"))
            {
                received.TrySetResult(msg);
                break;
            }
        });

        await Task.Delay(SubscriptionWarmup);

        await PublishAsync(host, topic: "calls.inbound.started");

        var msg = await received.Task.WaitAsync(ReceiveTimeout);
        msg.Subject.Should().Be("asterisk.sdk.calls.inbound.started");
        msg.Data.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Bridge_ShouldRespectSubjectPrefix_WhenConfigured()
    {
        const string customPrefix = "tenant42.events";

        using var host = BuildHost(prefix: customPrefix);
        await host.StartAsync();

        await using var client = await fixture.CreateClientAsync();
        var received = new TaskCompletionSource<NatsMsg<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var msg in client.SubscribeAsync<byte[]>($"{customPrefix}.>"))
            {
                received.TrySetResult(msg);
                break;
            }
        });

        await Task.Delay(SubscriptionWarmup);

        await PublishAsync(host, topic: "agents.42.state");

        var msg = await received.Task.WaitAsync(ReceiveTimeout);
        msg.Subject.Should().Be($"{customPrefix}.agents.42.state");
    }

    [Fact]
    public async Task Bridge_ShouldHandleMultipleEvents_WithoutLoss()
    {
        const int eventCount = 25;

        using var host = BuildHost(prefix: "asterisk.sdk");
        await host.StartAsync();

        await using var client = await fixture.CreateClientAsync();
        var received = new List<string>();
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var msg in client.SubscribeAsync<byte[]>("asterisk.sdk.queues.>"))
            {
                lock (received)
                {
                    received.Add(msg.Subject);
                    if (received.Count >= eventCount)
                    {
                        allDone.TrySetResult(true);
                        break;
                    }
                }
            }
        });

        await Task.Delay(SubscriptionWarmup);

        for (int i = 0; i < eventCount; i++)
            await PublishAsync(host, topic: $"queues.sales.event.{i}");

        await allDone.Task.WaitAsync(ReceiveTimeout);
        lock (received) { received.Should().HaveCount(eventCount); }
    }

    [Fact]
    public async Task Bridge_ShouldDispatchPayloadAsBytes_WithJsonEnvelope()
    {
        using var host = BuildHost(prefix: "asterisk.sdk");
        await host.StartAsync();

        await using var client = await fixture.CreateClientAsync();
        var received = new TaskCompletionSource<NatsMsg<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await foreach (var msg in client.SubscribeAsync<byte[]>("asterisk.sdk.demo.>"))
            {
                received.TrySetResult(msg);
                break;
            }
        });

        await Task.Delay(SubscriptionWarmup);
        await PublishAsync(host, topic: "demo.payload");

        var msg = await received.Task.WaitAsync(ReceiveTimeout);
        var body = System.Text.Encoding.UTF8.GetString(msg.Data!);
        body.Should().StartWith("{").And.EndWith("}");
        body.Should().Contain("demo.payload");
    }

    private IHost BuildHost(string prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAsteriskPush();
        builder.Services.AddPushNats(opt =>
        {
            opt.Url = fixture.Url;
            opt.SubjectPrefix = prefix;
            opt.ConnectTimeoutSeconds = 5;
        });
        return builder.Build();
    }

    private static async Task PublishAsync(IHost host, string topic)
    {
        var bus = host.Services.GetRequiredService<IPushEventBus>();
        var evt = new IntegrationTestEvent
        {
            Metadata = new PushEventMetadata(
                TenantId: "tenant-int",
                UserId: "user-int",
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: Guid.NewGuid().ToString("N"),
                TopicPath: topic),
        };
        await bus.PublishAsync(evt);
    }

    public sealed record IntegrationTestEvent : PushEvent
    {
        public override string EventType => "integration.test";
    }
}
