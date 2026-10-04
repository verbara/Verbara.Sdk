using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Verbara.Sdk.TestInfrastructure.Containers;

namespace Verbara.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Two Asterisk servers joined by a PJSIP trunk, and a SIPp caller, on a network of their own: server A, the PBX under
/// test (<c>docker/functional/multi-server/pbx/</c>), and server B, the far end in the PSTN emulator's role
/// (<c>docker/functional/pstn-emulator-config/</c>). Each has the AMI users of <see cref="MultiServerAmiUsers"/>,
/// with secrets generated for the run. No port is published; tests reach each server at its container address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Topology.</b> A's endpoint <c>agent</c> has its AOR contact at <see cref="FarNumber"/> on <c>server-b</c>, and
/// A's queue <c>q-trunk</c> has that endpoint as its one member. In A's context <c>lab</c>, <c>queue</c> joins the
/// queue and <c>direct</c> dials the endpoint; a SIP call from outside (SIPp) is dialled to it from
/// <c>from-sipp</c>. B's <see cref="FarNumber"/> rings 1 s, answers, holds 20 s and hangs up, so every leg A sends
/// over the trunk is answered on B (10 of 10 queue calls, measured on Asterisk 22 and 23).
/// </para>
/// <para>
/// <b>Naming.</b> The network is <see cref="RunName"/>, <c>{prefix}multi-{run}</c>: the prefix is <c>verbara-</c>,
/// or <c>VERBARA_FUNCTIONAL_DOCKER_PREFIX</c> when it is set, and <c>run</c> 8 hexadecimal characters. The containers
/// are <c>{RunName}-a</c>, <c>-b</c> and <c>-sipp</c>, and the configuration is written under a directory of the
/// temporary path with the same name, readable by its owner only where the platform supports it.
/// </para>
/// <para>
/// <b>Teardown.</b> <see cref="DisposeAsync"/> removes SIPp, B, A, the network and the configuration directory, each
/// on its own so a failure to remove one does not leak the rest, and does nothing the second time. A failed
/// <see cref="InitializeAsync"/> removes what it created before it rethrows, whether or not the caller disposes the
/// fixture afterwards. Neither depends on the test process's resource reaper.
/// </para>
/// </remarks>
public sealed class MultiServerFixture : IAsyncLifetime
{
    /// <summary>B's far number, which A's trunk endpoint dials.</summary>
    public const string FarNumber = "agent";

    /// <summary>A's dialplan context for calls the tests originate.</summary>
    public const string LabContext = "lab";

    private static readonly TimeSpan BootBound = TimeSpan.FromSeconds(90);

    private readonly Func<CancellationToken, Task>? _beforeServerBStarts;
    private readonly Dictionary<string, string> _secretsA;
    private readonly Dictionary<string, string> _secretsB;
    private bool _configWritten;

    // Released by DisposeAsync, which the caller (or xUnit, through a wrapper) calls through IAsyncLifetime.
    private INetwork? Network { get; set; }

    private AsteriskContainer? ServerA { get; set; }

    private AsteriskContainer? ServerB { get; set; }

    private SippContainer? SippContainerOfRun { get; set; }

    /// <summary>A fixture with a run name and secrets of its own; nothing is created before <see cref="InitializeAsync"/>.</summary>
    /// <param name="beforeServerBStarts">
    /// Called after A has booted and before B starts. A test makes it throw to see a failed start cleaned up.
    /// </param>
    public MultiServerFixture(Func<CancellationToken, Task>? beforeServerBStarts = null)
    {
        _beforeServerBStarts = beforeServerBStarts;
        var prefix = Environment.GetEnvironmentVariable("VERBARA_FUNCTIONAL_DOCKER_PREFIX") ?? "verbara-";
        RunName = prefix + "multi-" + Guid.NewGuid().ToString("N")[..8];
        ConfigDirectory = Path.Join(Path.GetTempPath(), RunName);
        _secretsA = MultiServerAmiUsers.All.ToDictionary(u => u.Name, _ => MultiServerAmiUsers.NewSecret(), StringComparer.Ordinal);
        _secretsB = MultiServerAmiUsers.All.ToDictionary(u => u.Name, _ => MultiServerAmiUsers.NewSecret(), StringComparer.Ordinal);
    }

    /// <summary>The run's name: the network's, the prefix of every container's, and the configuration directory's.</summary>
    public string RunName { get; }

    /// <summary>The directory the run's configuration is written to, removed at disposal.</summary>
    public string ConfigDirectory { get; }

    /// <summary>Server A's address on the run's network.</summary>
    public string ServerAAddress => (ServerA ?? throw NotStarted()).NetworkAddress;

    /// <summary>Server B's address on the run's network.</summary>
    public string ServerBAddress => (ServerB ?? throw NotStarted()).NetworkAddress;

    /// <summary>What <c>core show version</c> printed on server A.</summary>
    public string ServerAVersion { get; private set; } = "";

    /// <summary>What <c>core show version</c> printed on server B.</summary>
    public string ServerBVersion { get; private set; } = "";

    /// <summary>The SIPp container, on the run's network.</summary>
    public SippContainer Sipp => SippContainerOfRun ?? throw NotStarted();

    /// <summary>The run's secret for <paramref name="user"/> on server A.</summary>
    public string SecretA(AmiUserClass user) => _secretsA[(user ?? throw new ArgumentNullException(nameof(user))).Name];

    /// <summary>The run's secret for <paramref name="user"/> on server B.</summary>
    public string SecretB(AmiUserClass user) => _secretsB[(user ?? throw new ArgumentNullException(nameof(user))).Name];

    /// <summary>
    /// Writes the run's configuration, creates the network, starts A, calls the test hook, starts B and SIPp, and waits
    /// until both servers are fully booted. On any failure, removes what it created and rethrows.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            WriteConfiguration();
            var image = await AsteriskContainer.CreateImageAsync().ConfigureAwait(false);

            Network = new NetworkBuilder().WithName(RunName).Build();
            await Network.CreateAsync().ConfigureAwait(false);

            ServerA = new AsteriskContainer(Network, image, Path.Join(ConfigDirectory, "a"), RunName + "-a",
                publishPorts: false, networkAlias: "server-a");
            await ServerA.StartAsync().ConfigureAwait(false);
            ServerAVersion = await WaitFullyBootedAsync(ServerA).ConfigureAwait(false);

            using (var hook = new CancellationTokenSource(BootBound))
            {
                if (_beforeServerBStarts is not null)
                    await _beforeServerBStarts(hook.Token).ConfigureAwait(false);
            }

            ServerB = new AsteriskContainer(Network, image, Path.Join(ConfigDirectory, "b"), RunName + "-b",
                publishPorts: false, networkAlias: "server-b");
            await ServerB.StartAsync().ConfigureAwait(false);
            ServerBVersion = await WaitFullyBootedAsync(ServerB).ConfigureAwait(false);

            SippContainerOfRun = new SippContainer(Network, RunName + "-sipp");
            await SippContainerOfRun.StartAsync().ConfigureAwait(false);
        }
        catch (Exception startFailure)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (AggregateException cleanupFailure)
            {
                throw new AggregateException(startFailure, cleanupFailure);
            }

            throw;
        }
    }

    /// <summary>Removes SIPp, B, A, the network and the configuration directory; a second call does nothing.</summary>
    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();

        var sipp = SippContainerOfRun;
        SippContainerOfRun = null;
        if (sipp is not null)
            failures.AddRange(await TryAsync(() => sipp.DisposeAsync().AsTask()).ConfigureAwait(false));

        var serverB = ServerB;
        ServerB = null;
        if (serverB is not null)
            failures.AddRange(await TryAsync(() => serverB.DisposeAsync().AsTask()).ConfigureAwait(false));

        var serverA = ServerA;
        ServerA = null;
        if (serverA is not null)
            failures.AddRange(await TryAsync(() => serverA.DisposeAsync().AsTask()).ConfigureAwait(false));

        var network = Network;
        Network = null;
        if (network is not null)
            failures.AddRange(await TryAsync(() => network.DisposeAsync().AsTask()).ConfigureAwait(false));

        if (_configWritten)
        {
            _configWritten = false;
            failures.AddRange(await TryAsync(() =>
            {
                if (Directory.Exists(ConfigDirectory))
                    Directory.Delete(ConfigDirectory, recursive: true);
                return Task.CompletedTask;
            }).ConfigureAwait(false));
        }

        if (failures.Count > 0)
            throw new AggregateException($"The two-server fixture {RunName} did not remove everything it created.", failures);
    }

    /// <summary>Runs one removal; its failure is returned, not thrown, so the removals after it still run.</summary>
    private static async Task<IReadOnlyList<Exception>> TryAsync(Func<Task> remove)
    {
        try
        {
            await remove().ConfigureAwait(false);
            return [];
        }
        catch (Exception ex)
        {
            return [ex];
        }
    }

    private static async Task<string> WaitFullyBootedAsync(AsteriskContainer server)
    {
        using var ready = new CancellationTokenSource(BootBound);
        // The CLI command itself waits for the end of the start; it fails only while no Asterisk runs to take it.
        while ((await server.ExecAsync(["asterisk", "-rx", "core waitfullybooted"], ready.Token).ConfigureAwait(false)).ExitCode != 0)
            ready.Token.ThrowIfCancellationRequested();
        return (await server.ExecAsync(["asterisk", "-rx", "core show version"], ready.Token).ConfigureAwait(false)).Stdout.Trim();
    }

    /// <summary>
    /// Writes <c>a/</c> (A's tracked configuration) and <c>b/</c> (the PSTN emulator's) under
    /// <see cref="ConfigDirectory"/>, each with a <c>manager.conf</c> of the run's secrets in place of any tracked one.
    /// The run directory is its owner's alone where the platform supports it; the two server directories stay
    /// readable, because each container's Asterisk user reads them. <see cref="InitializeAsync"/> calls it; a second
    /// call does nothing, and <see cref="DisposeAsync"/> removes what it wrote.
    /// </summary>
    public void WriteConfiguration()
    {
        if (_configWritten)
            return;

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(ConfigDirectory);
        else
            Directory.CreateDirectory(ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _configWritten = true;

        CopyServer(DockerPaths.MultiServerPbxConfig, Path.Join(ConfigDirectory, "a"), _secretsA);
        CopyServer(DockerPaths.PstnEmulatorConfig, Path.Join(ConfigDirectory, "b"), _secretsB);
    }

    private static void CopyServer(string source, string target, IReadOnlyDictionary<string, string> secrets)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*.conf"))
        {
            var name = Path.GetFileName(file);
            if (!string.Equals(name, "manager.conf", StringComparison.Ordinal))
                File.Copy(file, Path.Join(target, name));
        }

        File.WriteAllText(Path.Join(target, "manager.conf"), MultiServerAmiUsers.WriteManagerConf(secrets));
    }

    private static InvalidOperationException NotStarted() => new("The two-server fixture has not been initialized.");
}
