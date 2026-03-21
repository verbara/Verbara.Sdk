namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.RealtimeDb;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Responses;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tests that PJSIP endpoints configured via PostgreSQL realtime (Sorcery/res_config_pgsql)
/// are visible through AMI after insertion, update, and deletion.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Realtime")]
public sealed class RealtimePjsipTests : FunctionalTestBase, IClassFixture<RealtimeFixture>
{
    private readonly RealtimeFixture _fixture;

    public RealtimePjsipTests(RealtimeFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [RealtimeFact]
    public async Task InsertEndpoint_ShouldBeVisibleViaAmi()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];
        try
        {
            // Insert endpoint and AOR into realtime DB
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow) VALUES (@Id, 'transport-udp', @Id, 'default', 'all', 'ulaw')",
                new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            // Query the endpoint via AMI — PJSIP realtime loads on demand via Sorcery
            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            var response = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            response.Output.Should().NotBeNullOrEmpty(
                "PJSIP endpoint inserted via realtime DB must be visible via AMI");
            response.Output.Should().NotContain("Unable to find",
                "endpoint must be found by Sorcery realtime lookup");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    [RealtimeFact]
    public async Task UpdateEndpoint_ShouldReflectChangeAfterReload()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];
        try
        {
            // Insert endpoint with initial callerid
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow, callerid) VALUES (@Id, 'transport-udp', @Id, 'default', 'all', 'ulaw', 'Original <1000>')",
                new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            // Verify initial state
            var before = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });
            before.Output.Should().NotBeNullOrEmpty(
                "endpoint must be visible before update");

            // Update callerid in DB and reload PJSIP
            await conn.ExecuteAsync(
                "UPDATE ps_endpoints SET callerid = 'Updated <2000>' WHERE id = @Id",
                new { Id = endpointId });
            await ami.SendActionAsync(new CommandAction { Command = "pjsip reload" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Query again — realtime re-reads from DB on demand
            var after = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            after.Output.Should().NotBeNullOrEmpty(
                "endpoint must still be visible after update");
            after.Output.Should().Contain("Updated",
                "updated callerid must be reflected after pjsip reload");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    [RealtimeFact]
    public async Task DeleteEndpoint_ShouldNotBeVisibleAfterReload()
    {
        var endpointId = $"test-rt-{Guid.NewGuid():N}"[..40];
        try
        {
            // Insert endpoint
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow) VALUES (@Id, 'transport-udp', @Id, 'default', 'all', 'ulaw')",
                new { Id = endpointId });
            await conn.ExecuteAsync(
                "INSERT INTO ps_aors (id, max_contacts) VALUES (@Id, 1)",
                new { Id = endpointId });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            // Confirm endpoint is visible before deletion
            var before = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });
            before.Output.Should().NotBeNullOrEmpty(
                "endpoint must be visible before deletion");
            before.Output.Should().NotContain("Unable to find",
                "endpoint must exist before deletion");

            // Delete from DB and reload
            await conn.ExecuteAsync("DELETE FROM ps_aors WHERE id = @Id", new { Id = endpointId });
            await conn.ExecuteAsync("DELETE FROM ps_endpoints WHERE id = @Id", new { Id = endpointId });
            await ami.SendActionAsync(new CommandAction { Command = "pjsip reload" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Query again — endpoint should no longer be found
            var after = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            // After deletion, output should either be empty/null or indicate the endpoint was not found
            var isGone = string.IsNullOrEmpty(after.Output) || after.Output.Contains("Unable to find");
            isGone.Should().BeTrue(
                "deleted PJSIP endpoint must not be visible after pjsip reload");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    private AmiConnection CreateRealtimeAmiConnection()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = RealtimeFixture.AmiHost,
            Port = RealtimeFixture.AmiPort,
            Username = RealtimeFixture.AmiUsername,
            Password = RealtimeFixture.AmiPassword,
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });
        return new AmiConnection(options, new PipelineSocketConnectionFactory(),
            LoggerFactory.CreateLogger<AmiConnection>());
    }
}
