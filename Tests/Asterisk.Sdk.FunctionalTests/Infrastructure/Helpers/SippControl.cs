namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics;

/// <summary>
/// Static helper for running SIPp scenarios via docker exec against the functional-sipp container.
/// The container uses network_mode: "service:asterisk" so 127.0.0.1:5060 is reachable.
/// </summary>
public static class SippControl
{
    private const string ContainerName = "functional-sipp";

    /// <summary>
    /// Run a SIPp scenario file against Asterisk inside the functional-sipp container.
    /// </summary>
    /// <param name="scenarioFile">XML scenario filename (must exist in /sipp-scenarios/ inside the container).</param>
    /// <param name="targetExtension">The SIP extension/number to call.</param>
    /// <param name="calls">Number of calls to place.</param>
    /// <param name="timeout">Maximum time to wait for SIPp to complete.</param>
    /// <returns>A <see cref="SippResult"/> with exit code, stdout, and stderr.</returns>
    public static async Task<SippResult> RunScenarioAsync(
        string scenarioFile,
        string targetExtension,
        int calls = 1,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var args = $"exec {ContainerName} sipp -sf /sipp-scenarios/{scenarioFile} " +
                   $"-s {targetExtension} 127.0.0.1:5060 " +
                   $"-m {calls} -l {calls} -r 1 -timeout {(int)timeout.Value.TotalSeconds}s " +
                   $"-trace_err -error_file /dev/stderr";

        var (exitCode, stdOut, stdErr) = await RunDockerAsync(args, timeout.Value + TimeSpan.FromSeconds(10));
        return new SippResult(exitCode, stdOut, stdErr);
    }

    /// <summary>
    /// Check whether the functional-sipp container is running and reachable.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"inspect --format={{{{.State.Running}}}} {ContainerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Trim() == "true";
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDockerAsync(
        string args, TimeSpan timeout)
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

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return (process.ExitCode, stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, string.Empty, "Timed out waiting for docker exec to complete");
        }
    }
}

/// <summary>Result of a SIPp scenario execution.</summary>
public sealed record SippResult(int ExitCode, string Output, string Error)
{
    /// <summary>Whether the scenario completed successfully (SIPp exit code 0).</summary>
    public bool Success => ExitCode == 0;
}
