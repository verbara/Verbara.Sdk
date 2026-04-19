using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps a SIPp container for SIP load/scenario testing. Requires a shared network.</summary>
public sealed class SippContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string ContainerName => _container.Name;

    public SippContainer(INetwork network)
    {
        _container = new ContainerBuilder("ctaloi/sipp")
            .WithNetwork(network)
            .WithBindMount(DockerPaths.SippScenariosDir, "/sipp-scenarios", AccessMode.ReadOnly)
            .WithEntrypoint("sleep", "infinity")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("true"))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    /// <summary>Runs a SIPp scenario XML file inside the container.</summary>
    /// <param name="scenarioFile">File name relative to /sipp-scenarios/.</param>
    /// <param name="targetHost">Hostname or IP of the SIP target inside the shared network.</param>
    /// <param name="targetPort">SIP port on the target.</param>
    /// <param name="extraArgs">Additional SIPp arguments.</param>
    public async Task<ExecResult> RunScenarioAsync(
        string scenarioFile,
        string targetHost,
        int targetPort = 5060,
        string[]? extraArgs = null,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "sipp",
            $"{targetHost}:{targetPort}",
            "-sf", $"/sipp-scenarios/{scenarioFile}",
            "-m", "1",
            "-timeout", "30"
        };

        if (extraArgs is not null)
            args.AddRange(extraArgs);

        return await _container.ExecAsync(args, ct).ConfigureAwait(false);
    }

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
