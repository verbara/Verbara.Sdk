using NATS.Client.Core;

using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Nats;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Verbara.Sdk.Push.Nats.Tests;

/// <summary>
/// Unit tests for the paths the DI container takes: the public constructor that builds the default
/// connection factories once, and the publisher that owns the real <see cref="NatsConnection"/>.
/// Neither needs a NATS server, because nothing connects until the host starts the bridge.
/// </summary>
public sealed class NatsBridgeLifetimeTests
{
    [Fact]
    public void Constructor_ShouldBuildABridgeThatHasNotStarted_WhenGivenALoggerFactory()
    {
        using var bus = BuildBus();
        var options = Options.Create(new NatsBridgeOptions { Url = "nats://127.0.0.1:4222" });

        using var bridge = new NatsBridge(
            bus,
            options,
            new DefaultNatsPayloadSerializer(options),
            new DefaultNatsPayloadDeserializer(),
            NullLoggerFactory.Instance);

        bridge.ExecuteTask.Should().BeNull("nothing connects until the host starts the bridge");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenLoggerFactoryIsNull()
    {
        using var bus = BuildBus();
        var options = Options.Create(new NatsBridgeOptions());

        var act = () => new NatsBridge(
            bus,
            options,
            new DefaultNatsPayloadSerializer(options),
            new DefaultNatsPayloadDeserializer(),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("loggerFactory");
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteAndRejectLaterPublishes_WhenTheConnectionNeverOpened()
    {
        var publisher = new NatsConnectionPublisher(new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" }));

        await publisher.DisposeAsync();
        await publisher.DisposeAsync();

        var publish = async () => await publisher.PublishAsync("asterisk.sdk.lifetime", [1]);
        await publish.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static RxPushEventBus BuildBus()
    {
        var busOptions = Options.Create(new PushEventBusOptions());
        return new RxPushEventBus(busOptions, NullLogger<RxPushEventBus>.Instance, new PushMetrics());
    }
}
