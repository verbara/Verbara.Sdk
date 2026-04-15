namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics;

public static class DockerControl
{
    // Set by FunctionalFixture.InitializeAsync() to the Testcontainers container name.
    // Falls back to "asterisk-sdk-test" for backwards-compat with docker-compose mode.
    public static string DefaultContainerName { get; set; } = "asterisk-sdk-test";

    public static Task KillContainerAsync(string? name = null)
        => RunDockerAsync($"kill {name ?? DefaultContainerName}");

    public static Task StartContainerAsync(string? name = null)
        => RunDockerAsync($"start {name ?? DefaultContainerName}");

    public static Task RestartContainerAsync(string? name = null)
        => RunDockerAsync($"restart {name ?? DefaultContainerName}");

    public static Task PauseContainerAsync(string? name = null)
        => RunDockerAsync($"pause {name ?? DefaultContainerName}");

    public static Task UnpauseContainerAsync(string? name = null)
        => RunDockerAsync($"unpause {name ?? DefaultContainerName}");

    public static async Task WaitForHealthyAsync(string? name = null, TimeSpan? timeout = null)
    {
        var containerName = name ?? DefaultContainerName;
        // Increase to 60s — Asterisk in Testcontainers takes longer than the previous
        // 30s default, especially in GitHub Actions CI after a container restart/kill.
        timeout ??= TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Use docker exec with ArgumentList so "core show uptime" is passed
                // as a single argument to asterisk -rx (required by Asterisk CLI).
                // This mirrors the Testcontainers wait strategy on AsteriskContainer and
                // works in distroless-adjacent environments with no /bin/sh.
                var result = await ExecInContainerAsync(containerName, "asterisk", "-rx", "core show uptime")
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result) && !result.Contains("Unable to connect"))
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
