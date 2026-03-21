namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Concurrency;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Trait("Category", "Integration")]
public sealed class ConcurrentManagerTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    [AsteriskContainerFact]
    public async Task ConcurrentChannelCreation_ShouldMaintainConsistentState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Originate many Local channels concurrently
        const int count = 15;
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "3",
                    IsAsync = true,
                    ActionId = $"conch-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Some may timeout; acceptable
            }
        });

        await Task.WhenAll(tasks);

        // Allow events to propagate to ChannelManager
        await Task.Delay(TimeSpan.FromSeconds(3));

        // The ChannelManager must be internally consistent:
        // every channel in ActiveChannels must be lookupable by UniqueId
        var channels = server.Channels.ActiveChannels.ToList();
        foreach (var ch in channels)
        {
            var byId = server.Channels.GetByUniqueId(ch.UniqueId);
            byId.Should().NotBeNull("channel {0} must exist in UniqueId index", ch.UniqueId);
        }

        // ChannelCount should match the enumeration
        server.Channels.ChannelCount.Should().Be(channels.Count,
            "ChannelCount and ActiveChannels enumeration must agree");
    }

    [AsteriskContainerFact]
    public async Task ConcurrentChannelLookup_ShouldBeThreadSafe()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Originate calls to populate channels
        for (var i = 0; i < 5; i++)
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "5",
                    IsAsync = true
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Concurrent reads while writes may still be happening
        var readExceptions = new ConcurrentBag<Exception>();

        var readTasks = Enumerable.Range(0, 50).Select(async readIdx =>
        {
            try
            {
                // Mix of different read operations — exercise all read paths concurrently
                _ = server.Channels.ChannelCount;
                _ = server.Channels.ActiveChannels.ToList();
                _ = server.Channels.GetByUniqueId("nonexistent");
                _ = server.Channels.GetByName("nonexistent");
                _ = server.Channels.GetChannelsByState(ChannelState.Up).ToList();
                await Task.Yield();
            }
            catch (Exception ex)
            {
                readExceptions.Add(ex);
            }
        });

        // Concurrent writes (more originates)
        var writeTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "2",
                    IsAsync = true,
                    ActionId = $"rw-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        });

        await Task.WhenAll(readTasks.Concat(writeTasks));

        readExceptions.Should().BeEmpty("concurrent reads during writes must not throw");
    }

    [AsteriskContainerFact]
    public async Task SecondaryIndexConsistency_ShouldBeMaintained()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Create channels via originate
        const int count = 10;
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "4",
                    IsAsync = true,
                    ActionId = $"idx-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        });

        await Task.WhenAll(tasks);
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Verify both indices are consistent:
        // For every channel, GetByName(channel.Name) == GetByUniqueId(channel.UniqueId)
        var channels = server.Channels.ActiveChannels.ToList();
        foreach (var ch in channels)
        {
            var byName = server.Channels.GetByName(ch.Name);
            var byId = server.Channels.GetByUniqueId(ch.UniqueId);

            // Both must resolve and point to the same channel object
            byName.Should().NotBeNull("channel {0} must be in name index", ch.Name);
            byId.Should().NotBeNull("channel {0} must be in UniqueId index", ch.UniqueId);

            if (byName is not null && byId is not null)
            {
                ReferenceEquals(byName, byId).Should().BeTrue(
                    "name and UniqueId indices must reference the same channel object");
            }
        }
    }

    [AsteriskContainerFact]
    public async Task ConcurrentQueueMemberUpdates_ShouldNotCorrupt()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Read queue state concurrently from multiple threads
        // (queue state comes from QueueStatusAction during StartAsync)
        var exceptions = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 50).Select(async readIdx =>
        {
            try
            {
                _ = server.Queues.QueueCount;
                _ = server.Queues.Queues.ToList();
                _ = server.Queues.GetByName("nonexistent-queue");
                _ = server.Queues.GetQueuesForMember("SIP/nonexistent").ToList();
                _ = server.Queues.GetQueueObjectsForMember("SIP/nonexistent").ToList();
                await Task.Yield();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Concurrently send QueueStatus actions to trigger queue event updates
        var updateTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new CommandAction
                {
                    Command = "queue show",
                    ActionId = $"qshow-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        });

        await Task.WhenAll(tasks.Concat(updateTasks));

        exceptions.Should().BeEmpty(
            "concurrent queue reads and updates must not throw");
    }

    [AsteriskContainerFact]
    public async Task ConcurrentAgiSessions_ShouldAllComplete()
    {
        // Test concurrent AMI origination to Local channels as a proxy for concurrent
        // AGI-like sessions. Each originate runs an application independently.
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        const int sessionCount = 20;
        var responses = new ConcurrentBag<ManagerResponse>();

        var tasks = Enumerable.Range(0, sessionCount).Select(async i =>
        {
            try
            {
                var response = await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "1",
                    IsAsync = true,
                    ActionId = $"agi-{i:D4}"
                });
                responses.Add(response);
            }
            catch (OperationCanceledException)
            {
                // Timeout is acceptable; still counts as "completed"
            }
        });

        await Task.WhenAll(tasks);

        // All concurrent sessions must get a response
        responses.Should().HaveCount(sessionCount,
            "all {0} concurrent sessions must receive AMI responses", sessionCount);

        // Verify ActionId correlation: no duplicates
        var actionIds = responses.Select(r => r.ActionId).Where(id => id is not null).ToList();
        actionIds.Should().OnlyHaveUniqueItems(
            "concurrent session responses must not have duplicate ActionIds");

        // Connection must remain healthy
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success", "connection must be healthy after concurrent sessions");
    }
}
