using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps an Asterisk realtime container built from Dockerfile.asterisk-realtime. Requires a shared network with PostgreSQL.</summary>
public sealed class AsteriskRealtimeContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public string ContainerName => _container.Name;

    public AsteriskRealtimeContainer(INetwork network)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk-realtime")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithNetwork(network)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(5038))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
