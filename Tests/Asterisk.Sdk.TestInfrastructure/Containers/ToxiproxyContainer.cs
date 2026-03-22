using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps a Toxiproxy container for fault injection testing.</summary>
public sealed class ToxiproxyContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int ApiPort => _container.GetMappedPublicPort(8474);
    public int ProxyPort => _container.GetMappedPublicPort(15038);
    public string ContainerName => _container.Name;

    public ToxiproxyContainer(INetwork? network = null)
    {
        var builder = new ContainerBuilder()
            .WithImage("ghcr.io/shopify/toxiproxy:2.9.0")
            .WithPortBinding(8474, true)
            .WithPortBinding(15038, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(8474));

        if (network is not null)
            builder = builder.WithNetwork(network);

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
