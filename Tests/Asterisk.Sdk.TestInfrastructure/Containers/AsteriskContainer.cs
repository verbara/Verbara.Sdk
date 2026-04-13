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
    public int AgiPort => _container.GetMappedPublicPort(4573);
    public string ContainerName => _container.Name;

    public AsteriskContainer(INetwork network, IImage image)
    {
        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            .WithPortBinding(4573, true)
            .WithBindMount(DockerPaths.AsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("asterisk")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(5038)
                    .UntilPortIsAvailable(8088))
            .Build();
    }

    public static async Task<IImage> CreateImageAsync(CancellationToken ct = default)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        await image.CreateAsync(ct).ConfigureAwait(false);
        return image;
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
