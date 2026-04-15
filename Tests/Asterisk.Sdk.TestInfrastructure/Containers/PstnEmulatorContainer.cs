using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>
/// Wraps a PSTN emulator container using the unified Asterisk image with file-based
/// pstn-emulator-config. Must share a network with the main Asterisk container.
/// Does NOT require Postgres — runs in file mode.
/// </summary>
public sealed class PstnEmulatorContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public string ContainerName => _container.Name;

    public PstnEmulatorContainer(INetwork network, IImage image)
    {
        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(5038, true)
            .WithBindMount(DockerPaths.PstnEmulatorConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithNetwork(network)
            .WithNetworkAliases("pstn-emulator")
            // UntilPortIsAvailable(5038) relies on /proc/net/tcp which is not populated
            // in GitHub Actions CI. Use the same Asterisk CLI readiness check as
            // AsteriskContainer — works reliably across all environments.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("asterisk", "-rx", "core show uptime"))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
