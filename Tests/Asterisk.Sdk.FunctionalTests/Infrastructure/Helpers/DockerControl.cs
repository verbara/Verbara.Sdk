namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics;
using Asterisk.Sdk.TestInfrastructure.Containers;

public static class DockerControl
{
    // Set by FunctionalTestFixture.InitializeAsync() to enable Testcontainers-native
    // lifecycle management. Null = fall back to docker CLI (docker-compose mode).
    public static AsteriskContainer? Container { get; set; }

    // Set by FunctionalTestFixture.InitializeAsync() to the Testcontainers container name.
    // Falls back to "asterisk-sdk-test" for backwards-compat with docker-compose mode.
    public static string DefaultContainerName { get; set; } = "asterisk-sdk-test";

    public static Task KillContainerAsync(string? name = null)
    {
        // Prefer Testcontainers StopAsync: sends SIGTERM + SIGKILL, container remains
        // restartable. docker kill via CLI sometimes leaves containers non-restartable
        // in CI because the daemon-managed lifecycle differs from the SDK's state machine.
        if (Container is not null && name is null)
            return Container.StopAsync();
        return RunDockerAsync($"kill {name ?? DefaultContainerName}");
    }

    public static async Task StartContainerAsync(string? name = null)
    {
        if (Container is not null && name is null)
        {
            // Testcontainers StartAsync re-applies the full wait strategy after restart
            // (CLI check + port check via UntilPortIsAvailable), ensuring Asterisk AND
            // the AMI module are both ready before returning.
            await Container.StartAsync().ConfigureAwait(false);
            // Refresh env vars — mapped host ports may be reassigned after a stop/start cycle.
            Environment.SetEnvironmentVariable("ASTERISK_HOST", Container.Host);
            Environment.SetEnvironmentVariable("ASTERISK_AMI_PORT",
                Container.AmiPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("ASTERISK_ARI_PORT",
                Container.AriPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        await RunDockerAsync($"start {name ?? DefaultContainerName}").ConfigureAwait(false);
    }

    public static async Task RestartContainerAsync(string? name = null)
    {
        if (Container is not null && name is null)
        {
            await Container.StopAsync().ConfigureAwait(false);
            await StartContainerAsync().ConfigureAwait(false); // reuse env-var refresh logic
            return;
        }
        await RunDockerAsync($"restart {name ?? DefaultContainerName}").ConfigureAwait(false);
    }

    public static Task PauseContainerAsync(string? name = null)
        => RunDockerAsync($"pause {name ?? DefaultContainerName}");

    public static Task UnpauseContainerAsync(string? name = null)
        => RunDockerAsync($"unpause {name ?? DefaultContainerName}");

    public static async Task WaitForHealthyAsync(string? name = null, TimeSpan? timeout = null)
    {
        // When Container is set, StartContainerAsync already invoked Container.StartAsync()
        // which re-applied the full Testcontainers wait strategy (CLI + UntilPortIsAvailable).
        // Both checks passed before StartAsync returned, so there is nothing more to poll.
        if (Container is not null && name is null)
            return;

        // Docker-compose / CLI mode: poll manually.
        var containerName = name ?? DefaultContainerName;
        // 60s — Asterisk may take time to fully start, especially after a restart in CI.
        timeout ??= TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + timeout.Value;

        // Read mapped host/port from env vars so the TCP probe targets the correct endpoint.
        var amiHost = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
        var amiPort = int.TryParse(
            Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var p) ? p : 5038;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Step 1: verify Asterisk CLI is responsive (core process running).
                // Uses ArgumentList so "core show uptime" is passed as a single argument
                // to asterisk -rx — required by the Asterisk CLI argument parser.
                var result = await ExecInContainerAsync(containerName, "asterisk", "-rx", "core show uptime")
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result) || result.Contains("Unable to connect"))
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    continue;
                }

                // Step 2: verify the AMI TCP port is accepting connections.
                // docker exec confirms the Asterisk process is up, but the AMI module
                // (manager.so) may not have started listening yet — a TCP probe here
                // prevents the next test from getting Connection refused on ConnectAsync.
                using var probe = new System.Net.Sockets.TcpClient();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                await probe.ConnectAsync(amiHost, amiPort, cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception) { /* container not yet ready — retry */ }
            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new TimeoutException($"Container {containerName} did not become ready within {timeout}");
    }

    private static async Task<string> ExecInContainerAsync(string containerName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(containerName);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output;
    }

    private static async Task<string> RunDockerAsync(string args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output;
    }
}
