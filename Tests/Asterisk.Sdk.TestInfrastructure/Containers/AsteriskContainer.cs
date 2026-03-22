using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps an Asterisk container built from Dockerfile.asterisk-file.</summary>
public sealed class AsteriskContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public int AriPort => _container.GetMappedPublicPort(8088);
    public int AgiPort => _container.GetMappedPublicPort(4573);
    public string ContainerName => _container.Name;

    public AsteriskContainer(INetwork? network = null)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk-file")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        var builder = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            .WithPortBinding(4573, true)
            .WithBindMount(DockerPaths.FunctionalAsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilPortIsAvailable(5038)
                    .UntilPortIsAvailable(8088));

        if (network is not null)
            builder = builder.WithNetwork(network);

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
