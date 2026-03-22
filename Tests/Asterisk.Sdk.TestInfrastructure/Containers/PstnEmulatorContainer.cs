using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps a PSTN emulator container (Asterisk image with pstn-emulator-config). Must share a network with the Asterisk container.</summary>
public sealed class PstnEmulatorContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public string ContainerName => _container.Name;

    public PstnEmulatorContainer(INetwork network)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfile("Dockerfile.asterisk-file")
            .WithDockerfileDirectory(DockerPaths.DockerDir)
            .Build();

        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithBindMount(DockerPaths.PstnEmulatorConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("pstn-emulator")
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
