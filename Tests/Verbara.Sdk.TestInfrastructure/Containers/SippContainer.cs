using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Verbara.Sdk.TestInfrastructure.Containers;

/// <summary>Wraps a SIPp container for SIP load/scenario testing. Requires a shared network.</summary>
public sealed class SippContainer : IAsyncDisposable
{
    /// <summary>The SIPp image, by digest (a single-architecture linux/amd64 manifest, resolved 2026-10-04).</summary>
    public const string Image = "ctaloi/sipp@sha256:c459f2340443ddcc159227efc798217dbdaad0dbe88b76b78b1a876aa271986a";

    private readonly IContainer _container;

    public string ContainerName => _container.Name;

    /// <summary>A SIPp container on <paramref name="network"/>, idle until a scenario is run in it.</summary>
    /// <param name="network">The network the container joins.</param>
    /// <param name="name">The container's name; Testcontainers chooses one by default.</param>
    public SippContainer(INetwork network, string? name = null)
    {
        var builder = new ContainerBuilder(Image);
        if (name is not null)
            builder = builder.WithName(name);

        _container = builder
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

    /// <summary>
    /// Places <paramref name="calls"/> calls with SIPp's built-in <c>uac</c> scenario, one at a time: each dials
    /// <paramref name="number"/> at <paramref name="targetHost"/>, talks 2 s once answered and hangs up. SIPp exits 0
    /// when every call succeeded; its statistics, with the <c>Successful call</c> and <c>Failed call</c> counts, are
    /// on standard output.
    /// </summary>
    /// <param name="targetHost">The SIP target's name or address on the shared network.</param>
    /// <param name="number">The number dialled (the Request-URI's user part).</param>
    /// <param name="calls">How many calls to place.</param>
    /// <param name="targetPort">The SIP port on the target.</param>
    /// <param name="ct">Cancels the exec.</param>
    public Task<ExecResult> RunUacAsync(
        string targetHost,
        string number,
        int calls = 1,
        int targetPort = 5060,
        CancellationToken ct = default)
    {
        IList<string> args =
        [
            "sipp", "-sn", "uac", "-s", number, $"{targetHost}:{targetPort}",
            "-m", calls.ToString(System.Globalization.CultureInfo.InvariantCulture), "-l", "1", "-d", "2000",
            "-timeout", "30s",
        ];
        return _container.ExecAsync(args, ct);
    }

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
