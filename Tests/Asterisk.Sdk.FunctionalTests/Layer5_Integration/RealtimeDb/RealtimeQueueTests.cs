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
/// Tests that Asterisk realtime queues and members configured via PostgreSQL
/// are visible through AMI after a queue reload.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Realtime")]
public sealed class RealtimeQueueTests : FunctionalTestBase, IClassFixture<RealtimeFixture>
{
    private readonly RealtimeFixture _fixture;

    public RealtimeQueueTests(RealtimeFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [RealtimeFact]
    public async Task InsertQueueWithMember_ShouldBeVisibleViaAmi()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];
        try
        {
            // Insert queue and member into realtime DB
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 15)",
                new { Name = queueName });
            await conn.ExecuteAsync(
                "INSERT INTO queue_members (queue_name, interface, membername, penalty, paused) VALUES (@Queue, 'Local/100@default', 'TestAgent', 0, 0)",
                new { Queue = queueName });

            // Reload queues via AMI
            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify queue is visible
            var response = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"queue show {queueName}" });

            response.Output.Should().NotBeNullOrEmpty(
                "queue inserted via realtime DB must be visible after reload");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
        }
    }

    [RealtimeFact]
    public async Task AddMemberViaDb_ShouldAppearInQueueStatus()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];
        try
        {
            // Insert queue with no members
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 15)",
                new { Name = queueName });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Now add a member via DB and reload again
            await conn.ExecuteAsync(
                "INSERT INTO queue_members (queue_name, interface, membername, penalty, paused) VALUES (@Queue, 'Local/200@default', 'DynamicAgent', 0, 0)",
                new { Queue = queueName });
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify member appears in queue show output
            var response = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"queue show {queueName}" });

            response.Output.Should().NotBeNullOrEmpty(
                "queue must be visible after reload");
            response.Output.Should().Contain("Local/200@default",
                "member added via DB must appear in queue show after reload");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
        }
    }

    [RealtimeFact]
    public async Task RemoveMemberViaDb_ShouldDisappearAfterReload()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];
        try
        {
            // Insert queue with one member
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 15)",
                new { Name = queueName });
            await conn.ExecuteAsync(
                "INSERT INTO queue_members (queue_name, interface, membername, penalty, paused) VALUES (@Queue, 'Local/300@default', 'RemovableAgent', 0, 0)",
                new { Queue = queueName });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Confirm member is present
            var before = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"queue show {queueName}" });
            before.Output.Should().Contain("Local/300@default",
                "member must be present before removal");

            // Remove member from DB and reload
            await conn.ExecuteAsync(
                "DELETE FROM queue_members WHERE queue_name = @Queue AND interface = 'Local/300@default'",
                new { Queue = queueName });
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify member no longer appears
            var after = await ami.SendActionAsync<CommandResponse>(
                new CommandAction { Command = $"queue show {queueName}" });

            after.Output.Should().NotBeNullOrEmpty(
                "queue must still be visible after member removal");
            after.Output.Should().NotContain("Local/300@default",
                "member removed from DB must disappear after queue reload");
        }
        finally
        {
            await _fixture.CleanupTestQueueAsync(queueName);
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
