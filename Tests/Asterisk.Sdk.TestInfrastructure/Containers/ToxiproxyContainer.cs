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
            // Allow Toxiproxy to reach Asterisk AMI via the host-mapped port using the
            // Docker-standard host.docker.internal DNS name (resolves to the Docker host
            // gateway). This avoids relying on Docker's embedded DNS for the 'asterisk'
            // alias, which can fail from distroless Go images after container restarts.
            .WithExtraHost("host.docker.internal", "host-gateway")
            // ghcr.io/shopify/toxiproxy:2.9.0 is a distroless Go image — no /bin/sh.
            // UntilCommandIsCompleted("/toxiproxy", "--version") only confirms the binary
            // exists but exits immediately without starting the server, causing a race where
            // ToxiproxyFixture.InitializeAsync() calls the REST API before it is ready.
            // UntilHttpRequestIsSucceeded polls GET /proxies on port 8474 from the host side
            // (no shell needed), returning only once the HTTP server is accepting requests.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(req => req.ForPort(8474).ForPath("/proxies")));

        if (network is not null)
            builder = builder.WithNetwork(network);

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
