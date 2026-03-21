namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics;

public static class DockerControl
{
    private const string ContainerName = "functional-asterisk";

    public static Task KillContainerAsync(string name = ContainerName)
        => RunDockerAsync($"kill {name}");

    public static Task StartContainerAsync(string name = ContainerName)
        => RunDockerAsync($"start {name}");

    public static Task RestartContainerAsync(string name = ContainerName)
        => RunDockerAsync($"restart {name}");

    public static Task PauseContainerAsync(string name = ContainerName)
        => RunDockerAsync($"pause {name}");

    public static Task UnpauseContainerAsync(string name = ContainerName)
        => RunDockerAsync($"unpause {name}");

    public static async Task WaitForHealthyAsync(string name = ContainerName,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < deadline)
        {
            var health = await RunDockerAsync(
                $"inspect --format={{{{.State.Health.Status}}}} {name}");
            if (health.Trim() == "healthy") return;
            await Task.Delay(1000);
        }

        throw new TimeoutException($"Container {name} did not become healthy within {timeout}");
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

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
