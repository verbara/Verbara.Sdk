using Verbara.Sdk.TestInfrastructure;
using Verbara.Sdk.TestInfrastructure.Stacks;
using FluentAssertions;

namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.MultiServer;

/// <summary>
/// The two-server fixture's per-run AMI secrets, without Docker: no category, so it runs in the unit lane.
/// </summary>
public sealed class MultiServerConfigurationTests
{
    [Fact]
    public async Task WriteConfiguration_ShouldGiveEveryUserASecretOfItsOwnRun_WhenTwoRunsAreWritten()
    {
        var first = new MultiServerFixture();
        var second = new MultiServerFixture();
        try
        {
            first.WriteConfiguration();
            second.WriteConfiguration();

            var secrets = new List<string>();
            foreach (var run in new[] { first, second })
            {
                var managerA = await File.ReadAllTextAsync(Path.Join(run.ConfigDirectory, "a", "manager.conf"));
                var managerB = await File.ReadAllTextAsync(Path.Join(run.ConfigDirectory, "b", "manager.conf"));
                foreach (var user in MultiServerAmiUsers.All)
                {
                    managerA.Should().Contain($"[{user.Name}]\nsecret = {run.SecretA(user)}\n");
                    managerB.Should().Contain($"[{user.Name}]\nsecret = {run.SecretB(user)}\n");
                    secrets.Add(run.SecretA(user));
                    secrets.Add(run.SecretB(user));
                }
            }

            secrets.Should().OnlyHaveUniqueItems("two runs, two servers and four users never share a secret");
            var tracked = Directory.EnumerateFiles(DockerPaths.DockerDir, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText).ToList();
            tracked.Should().NotContain(text => secrets.Any(text.Contains), "no secret of a run is written into the tree");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        Directory.Exists(first.ConfigDirectory).Should().BeFalse();
        Directory.Exists(second.ConfigDirectory).Should().BeFalse();
    }
}
