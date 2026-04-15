using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>
/// Wraps the unified Asterisk 22 container running in Realtime mode
/// (PostgreSQL-backed PJSIP). Requires a shared network with PostgresContainer.
/// </summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public int AriPort => _container.GetMappedPublicPort(8088);
    public string ContainerName => _container.Name;

    public AsteriskContainer(INetwork network, IImage image)
    {
        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            // Linux Docker: route host.docker.internal → host gateway so Asterisk can reach the FastAGI test server.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithBindMount(DockerPaths.AsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("asterisk")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // Step 1: Asterisk core process is running.
                    .UntilCommandIsCompleted("asterisk", "-rx", "core show uptime")
                    // Step 2: AMI TCP port is accepting connections from the host.
                    // This ensures the manager.so module is fully loaded before StartAsync returns,
                    // including after a StopAsync/StartAsync restart cycle in reconnection tests.
                    .UntilPortIsAvailable(5038))
            .Build();
    }

    // Cache the image build so parallel fixture initializations (e.g. FunctionalCollection +
    // RealtimeCollection in the same test assembly) share one build instead of competing.
    private static IImage? _cachedImage;
    private static readonly SemaphoreSlim _buildLock = new(1, 1);

    public static async Task<IImage> CreateImageAsync(CancellationToken ct = default)
    {
        if (_cachedImage is not null) return _cachedImage;

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedImage is not null) return _cachedImage;

            var image = new ImageFromDockerfileBuilder()
                .WithDockerfile("Dockerfile.asterisk")
                .WithDockerfileDirectory(DockerPaths.DockerDir)
                .Build();

            await image.CreateAsync(ct).ConfigureAwait(false);
            _cachedImage = image;
            return image;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    /// <summary>Stops the container (graceful SIGTERM + SIGKILL). Preserves the container so it can be restarted.</summary>
    public Task StopAsync(CancellationToken ct = default) => _container.StopAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
