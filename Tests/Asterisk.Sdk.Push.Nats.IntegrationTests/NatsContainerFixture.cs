using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using Xunit;

namespace Asterisk.Sdk.Push.Nats.IntegrationTests;

/// <summary>
/// Testcontainers-backed NATS fixture. Spins up <c>nats:2.10-alpine</c> in a disposable
/// container and exposes a ready-to-use <see cref="NatsConnection"/> for assertions.
/// </summary>
public sealed class NatsContainerFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string Url =>
        $"nats://{_container!.Hostname}:{_container.GetMappedPublicPort(4222)}";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("nats:2.10-alpine")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilInternalTcpPortIsAvailable(4222))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Open a fresh NATS client connection against the container.</summary>
    public async Task<NatsConnection> CreateClientAsync()
    {
        var conn = new NatsConnection(new NatsOpts { Url = Url });
        await conn.ConnectAsync();
        return conn;
    }
}

#pragma warning disable CA1711 // xunit convention
[CollectionDefinition("Nats")]
public class NatsCollection : ICollectionFixture<NatsContainerFixture>;
#pragma warning restore CA1711
