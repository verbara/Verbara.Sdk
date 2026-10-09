using System.Diagnostics;
using Verbara.Sdk.TestInfrastructure.Stacks;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// The two-server fixture leaves nothing of a run behind: each test creates, initialises and disposes its own
/// instance, then asks the Docker host and the file system for anything that carries the run's name.
/// </summary>
[Trait("Category", "Functional")]
public sealed class MultiServerTeardownTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Dispose_ShouldRemoveEveryContainerNetworkAndDirectory_WhenTheRunStarted()
    {
        var lab = new MultiServerFixture();
        try
        {
            await lab.InitializeAsync();
            await lab.StartAmiPathProxyAsync();
            output.WriteLine($"{lab.RunName}: A {lab.ServerAVersion}; B {lab.ServerBVersion}");
            (await ContainersAsync(lab.RunName)).Should().HaveCount(4, "A, B, SIPp and the AMI proxy run under the run's name");
            (await NetworksAsync(lab.RunName)).Should().ContainSingle();
            Directory.Exists(lab.ConfigDirectory).Should().BeTrue();
        }
        finally
        {
            await lab.DisposeAsync();
        }

        await lab.DisposeAsync();
        await AssertNothingLeftAsync(lab);
    }

    [Fact]
    public async Task InitializeAsync_ShouldRemoveWhatItCreated_WhenServerBFailsToStart()
    {
        var lab = new MultiServerFixture(beforeServerBStarts: _ =>
            throw new InvalidOperationException("server B fails to start (test hook)"));

        var initialize = async () => await lab.InitializeAsync();

        await initialize.Should().ThrowAsync<InvalidOperationException>().WithMessage("server B fails to start*");
        await AssertNothingLeftAsync(lab);
        await lab.DisposeAsync();
    }

    private async Task AssertNothingLeftAsync(MultiServerFixture lab)
    {
        var containers = await ContainersAsync(lab.RunName);
        var networks = await NetworksAsync(lab.RunName);
        output.WriteLine($"{lab.RunName}: {containers.Count} containers, {networks.Count} networks, directory exists: {Directory.Exists(lab.ConfigDirectory)}");

        new { Containers = containers, Networks = networks, Directory = Directory.Exists(lab.ConfigDirectory) }
            .Should().BeEquivalentTo(
                new { Containers = Array.Empty<string>(), Networks = Array.Empty<string>(), Directory = false },
                $"disposal removes every container, network and directory named {lab.RunName}, without the reaper");
    }

    private static Task<IReadOnlyList<string>> ContainersAsync(string runName) =>
        DockerAsync("ps", "-a", "--filter", $"name={runName}", "--format", "{{.Names}}");

    private static Task<IReadOnlyList<string>> NetworksAsync(string runName) =>
        DockerAsync("network", "ls", "--filter", $"name={runName}", "--format", "{{.Name}}");

    private static async Task<IReadOnlyList<string>> DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("docker did not start");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker {string.Join(' ', args)} exited {process.ExitCode}: {stderr}");
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
