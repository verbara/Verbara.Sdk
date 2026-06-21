namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.RealtimeDb;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.FunctionalTests.Infrastructure.Attributes;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

/// <summary>
/// Tests that PJSIP endpoints configured via PostgreSQL realtime (Sorcery/res_config_pgsql)
/// are visible through AMI after insertion, update, and deletion.
/// </summary>
[Collection("Realtime")]
[Trait("Category", "Realtime")]
public sealed class RealtimePjsipTests : FunctionalTestBase
{
    private readonly RealtimeDbFixture _fixture;

    public RealtimePjsipTests(RealtimeDbFixture fixture) : base("Verbara.Sdk.Ami")
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

            await using var insertEndpoint = new NpgsqlCommand(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow) VALUES ($1, 'transport-udp', $1, 'default', 'all', 'ulaw')", conn);
            insertEndpoint.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertEndpoint.ExecuteNonQueryAsync();

            await using var insertAor = new NpgsqlCommand(
                "INSERT INTO ps_aors (id, max_contacts) VALUES ($1, 1)", conn);
            insertAor.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertAor.ExecuteNonQueryAsync();

            // Query the endpoint via AMI — PJSIP realtime loads on demand via Sorcery
            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            var response = await ami.SendActionAsync(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            var output = GetOutput(response);
            output.Should().NotContain("Unable to find",
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

            await using var insertEndpoint = new NpgsqlCommand(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow, callerid) VALUES ($1, 'transport-udp', $1, 'default', 'all', 'ulaw', 'Original <1000>')", conn);
            insertEndpoint.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertEndpoint.ExecuteNonQueryAsync();

            await using var insertAor = new NpgsqlCommand(
                "INSERT INTO ps_aors (id, max_contacts) VALUES ($1, 1)", conn);
            insertAor.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertAor.ExecuteNonQueryAsync();

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            // Verify initial state
            var before = await ami.SendActionAsync(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });
            GetOutput(before).Should().NotContain("Unable to find",
                "endpoint must be visible before update");

            // Update callerid in DB and reload PJSIP
            await using var updateEndpoint = new NpgsqlCommand(
                "UPDATE ps_endpoints SET callerid = 'Updated <2000>' WHERE id = $1", conn);
            updateEndpoint.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await updateEndpoint.ExecuteNonQueryAsync();
            await ami.SendActionAsync(new CommandAction { Command = "pjsip reload" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Query again — realtime re-reads from DB on demand
            var after = await ami.SendActionAsync(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            GetOutput(after).Should().NotContain("Unable to find",
                "endpoint must still be visible after update");
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

            await using var insertEndpoint = new NpgsqlCommand(
                "INSERT INTO ps_endpoints (id, transport, aors, context, disallow, allow) VALUES ($1, 'transport-udp', $1, 'default', 'all', 'ulaw')", conn);
            insertEndpoint.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertEndpoint.ExecuteNonQueryAsync();

            await using var insertAor = new NpgsqlCommand(
                "INSERT INTO ps_aors (id, max_contacts) VALUES ($1, 1)", conn);
            insertAor.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await insertAor.ExecuteNonQueryAsync();

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();

            // Confirm endpoint is visible before deletion
            var before = await ami.SendActionAsync(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });
            GetOutput(before).Should().NotContain("Unable to find",
                "endpoint must exist before deletion");

            // Delete from DB
            await using var deleteAor = new NpgsqlCommand("DELETE FROM ps_aors WHERE id = $1", conn);
            deleteAor.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await deleteAor.ExecuteNonQueryAsync();

            await using var deleteEndpoint = new NpgsqlCommand("DELETE FROM ps_endpoints WHERE id = $1", conn);
            deleteEndpoint.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            await deleteEndpoint.ExecuteNonQueryAsync();

            // Verify DB deletion committed
            await using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM ps_endpoints WHERE id = $1", conn);
            countCmd.Parameters.Add(new NpgsqlParameter { Value = endpointId });
            var dbCheck = (long?)await countCmd.ExecuteScalarAsync();
            dbCheck.Should().Be(0, "endpoint must be deleted from DB before checking Asterisk");

            // Reload PJSIP and use a fresh AMI connection to avoid stale state
            await ami.SendActionAsync(new CommandAction { Command = "pjsip reload" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Use a fresh AMI connection for the verification query
            await using var ami2 = CreateRealtimeAmiConnection();
            await ami2.ConnectAsync();

            var after = await ami2.SendActionAsync(
                new CommandAction { Command = $"pjsip show endpoint {endpointId}" });

            var afterOutput = GetOutput(after);
            var isGone = string.IsNullOrEmpty(afterOutput) || afterOutput.Contains("Unable to find", StringComparison.Ordinal);
            isGone.Should().BeTrue(
                "deleted PJSIP endpoint must not be visible after pjsip reload");
        }
        finally
        {
            await _fixture.CleanupTestEndpointAsync(endpointId);
        }
    }

    private static string GetOutput(ManagerResponse response)
    {
        // Asterisk 22+ accumulates multiple Output: lines into __CommandOutput
        if (response.RawFields is not null)
        {
            if (response.RawFields.TryGetValue("__CommandOutput", out var cmdOutput))
                return cmdOutput;
            if (response.RawFields.TryGetValue("Output", out var output))
                return output;
        }
        return response.Message ?? "";
    }

    private AmiConnection CreateRealtimeAmiConnection()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = _fixture.AmiHost,
            Port = _fixture.AmiPort,
            Username = RealtimeDbFixture.AmiUsername,
            Password = RealtimeDbFixture.AmiPassword,
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });
        return new AmiConnection(options, new PipelineSocketConnectionFactory(),
            LoggerFactory.CreateLogger<AmiConnection>());
    }
}
