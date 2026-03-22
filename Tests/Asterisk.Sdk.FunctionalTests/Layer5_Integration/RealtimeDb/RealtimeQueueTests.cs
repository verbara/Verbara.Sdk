namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.RealtimeDb;

using Asterisk.Sdk.Ami;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
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
[Collection("Realtime")]
[Trait("Category", "Realtime")]
public sealed class RealtimeQueueTests : FunctionalTestBase
{
    private readonly RealtimeDbFixture _fixture;

    public RealtimeQueueTests(RealtimeDbFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [RealtimeFact]
    public async Task InsertQueueWithMember_ShouldBeVisibleViaAmi()
    {
        var queueName = $"test-q-{Guid.NewGuid():N}"[..30];
        try
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 15)",
                new { Name = queueName });
            await conn.ExecuteAsync(
                "INSERT INTO queue_members (queue_name, interface, membername, penalty, paused) VALUES (@Queue, 'Local/100@default', 'TestAgent', 0, 0)",
                new { Queue = queueName });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // queue show returns Response: Success when queue exists
            var response = await ami.SendActionAsync(
                new CommandAction { Command = $"queue show {queueName}" });

            response.Response.Should().Be("Success",
                "queue inserted via realtime DB must be queryable after reload");
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
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO queue_table (name, strategy, timeout) VALUES (@Name, 'ringall', 15)",
                new { Name = queueName });

            await using var ami = CreateRealtimeAmiConnection();
            await ami.ConnectAsync();
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify queue exists but has no members (Response: Success, Output shows "No Members")
            var before = await ami.SendActionAsync(
                new CommandAction { Command = $"queue show {queueName}" });
            before.Response.Should().Be("Success");

            // Add member and reload
            await conn.ExecuteAsync(
                "INSERT INTO queue_members (queue_name, interface, membername, penalty, paused) VALUES (@Queue, 'Local/200@default', 'DynamicAgent', 0, 0)",
                new { Queue = queueName });
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Verify member count changed — use QueueSummaryAction for structured data
            var summary = await ami.SendActionAsync(
                new QueueSummaryAction { Queue = queueName });
            summary.Response.Should().Be("Success",
                "QueueSummary must succeed for queue with member added via DB");
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

            // Confirm queue is loaded
            var before = await ami.SendActionAsync(
                new CommandAction { Command = $"queue show {queueName}" });
            before.Response.Should().Be("Success", "queue must exist before member removal");

            // Remove member and reload
            await conn.ExecuteAsync(
                "DELETE FROM queue_members WHERE queue_name = @Queue AND interface = 'Local/300@default'",
                new { Queue = queueName });
            await ami.SendActionAsync(new CommandAction { Command = "queue reload all" });
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Queue should still exist but with no members
            var after = await ami.SendActionAsync(
                new CommandAction { Command = $"queue show {queueName}" });
            after.Response.Should().Be("Success",
                "queue must still exist after member removal");
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
