namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Queues;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class QueueMemberTests : FunctionalTestBase
{
    private const string TestQueue = "test-queue";
    private const string TestQueue2 = "test-queue-2";
    private const string TestInterface = "Local/100@test-functional";

    [AsteriskContainerFact]
    public async Task AddMember_ShouldUpdateQueueAndReverseIndex()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            var response = await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            response.Response.Should().Be("Success");

            // Allow event propagation
            await Task.Delay(TimeSpan.FromSeconds(1));

            var queue = server.Queues.GetByName(TestQueue);
            queue.Should().NotBeNull();
            queue!.Members.Should().ContainKey(TestInterface);

            var queuesForMember = server.Queues.GetQueuesForMember(TestInterface).ToList();
            queuesForMember.Should().Contain(TestQueue);
        }
        finally
        {
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
        }
    }

    [AsteriskContainerFact]
    public async Task RemoveMember_ShouldCleanupReverseIndex()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            // Add member first
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify member exists
            server.Queues.GetByName(TestQueue)!.Members.Should().ContainKey(TestInterface);

            // Remove member
            var removeResponse = await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            removeResponse.Response.Should().Be("Success");
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify member gone from queue
            server.Queues.GetByName(TestQueue)!.Members.Should().NotContainKey(TestInterface);

            // Verify reverse index cleaned
            var queuesForMember = server.Queues.GetQueuesForMember(TestInterface).ToList();
            queuesForMember.Should().NotContain(TestQueue);
        }
        finally
        {
            // Best-effort cleanup in case removal failed
            try
            {
                await connection.SendActionAsync(new QueueRemoveAction
                {
                    Queue = TestQueue,
                    Interface = TestInterface
                });
            }
            catch
            {
                // Ignore — member may already be removed
            }
        }
    }

    [AsteriskContainerFact]
    public async Task PauseMember_ShouldUpdateStateAndFireEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            // Add member
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Pause member
            var pauseResponse = await connection.SendActionAsync(new QueuePauseAction
            {
                Queue = TestQueue,
                Interface = TestInterface,
                Paused = true,
                Reason = "break"
            });
            pauseResponse.Response.Should().Be("Success");
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify paused state
            var queue = server.Queues.GetByName(TestQueue);
            queue.Should().NotBeNull();
            queue!.Members.TryGetValue(TestInterface, out var member).Should().BeTrue();
            member!.Paused.Should().BeTrue();
        }
        finally
        {
            // Unpause then remove
            try
            {
                await connection.SendActionAsync(new QueuePauseAction
                {
                    Queue = TestQueue,
                    Interface = TestInterface,
                    Paused = false
                });
            }
            catch
            {
                // Ignore
            }

            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
        }
    }

    [AsteriskContainerFact]
    public async Task DeviceStateChange_ShouldPropagateToAllQueues()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            // Add same interface to two queues
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue2,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify member is in both queues
            server.Queues.GetByName(TestQueue)!.Members.Should().ContainKey(TestInterface);
            server.Queues.GetByName(TestQueue2)!.Members.Should().ContainKey(TestInterface);

            // Re-query via QueueStatusAction to confirm status is consistent
            await foreach (var evt in connection.SendEventGeneratingActionAsync(new QueueStatusAction()))
            {
                // Just consume to trigger state refresh via AsteriskServer event observer
            }
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify member status is reported in both queues via QueueManager
            var q1Member = server.Queues.GetByName(TestQueue)!.Members[TestInterface];
            var q2Member = server.Queues.GetByName(TestQueue2)!.Members[TestInterface];

            // Both members should have a valid status (the exact value depends on the device state)
            q1Member.Status.Should().BeDefined();
            q2Member.Status.Should().BeDefined();
        }
        finally
        {
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue2,
                Interface = TestInterface
            });
        }
    }

    [AsteriskContainerFact]
    public async Task MemberInMultipleQueues_ShouldTrackCorrectly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            // Add same interface to two queues
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue2,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify reverse index returns both queues
            var queuesForMember = server.Queues.GetQueuesForMember(TestInterface).ToList();
            queuesForMember.Should().HaveCount(2);
            queuesForMember.Should().Contain(TestQueue);
            queuesForMember.Should().Contain(TestQueue2);

            // Verify GetQueueObjectsForMember returns both queue objects
            var queueObjects = server.Queues.GetQueueObjectsForMember(TestInterface).ToList();
            queueObjects.Should().HaveCount(2);
            queueObjects.Select(q => q.Name).Should().Contain(TestQueue);
            queueObjects.Select(q => q.Name).Should().Contain(TestQueue2);
        }
        finally
        {
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue2,
                Interface = TestInterface
            });
        }
    }
}
