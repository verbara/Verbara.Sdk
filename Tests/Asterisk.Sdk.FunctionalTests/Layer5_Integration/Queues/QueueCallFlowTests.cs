namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Queues;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Queue call flow tests that verify the full event sequence when callers enter queues.
/// Uses OriginateAction with Local channels to drive call flow (no SIPp dependency).
/// </summary>
[Trait("Category", "Integration")]
public sealed class QueueCallFlowTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    private const string TestQueue = "test-queue";
    private const string TestInterface = "Local/100@test-functional";

    public QueueCallFlowTests() : base("Asterisk.Sdk.Live")
    {
    }

    [AsteriskContainerFact]
    public async Task OriginateCallToQueue_ShouldProduceFullEventSequence()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.Queues.CallerJoined += (queueName, _) =>
        {
            if (string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                joinTcs.TrySetResult(true);
        };
        server.Queues.CallerLeft += (queueName, _) =>
        {
            if (string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                leaveTcs.TrySetResult(true);
        };

        try
        {
            // Add a member so the queue has someone to route to
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Originate a call to queue extension (500 = Queue(test-queue))
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/500@test-functional",
                Context = "test-functional",
                Exten = "500",
                Priority = 1,
                IsAsync = true,
                Timeout = 15000
            });

            // Verify caller joins the queue
            var joinResult = await Task.WhenAny(joinTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            joinResult.Should().Be(joinTcs.Task, "caller should join the queue");

            // Hangup all channels to trigger leave
            await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

            // Verify caller leaves
            var leaveResult = await Task.WhenAny(leaveTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            leaveResult.Should().Be(leaveTcs.Task, "caller should leave the queue after hangup");
        }
        finally
        {
            try
            {
                await connection.SendActionAsync(new QueueRemoveAction
                {
                    Queue = TestQueue,
                    Interface = TestInterface
                });
            }
            catch { /* best effort cleanup */ }
        }
    }

    [AsteriskContainerFact]
    public async Task MultipleCallersInQueue_ShouldMaintainOrder()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var positions = new List<int>();
        var positionLock = new Lock();
        var joinCount = 0;
        var allJoinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.Queues.CallerJoined += (queueName, entry) =>
        {
            if (!string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                return;
            lock (positionLock)
            {
                positions.Add(entry.Position);
                joinCount++;
                if (joinCount >= 3)
                    allJoinedTcs.TrySetResult(true);
            }
        };

        try
        {
            // No members — callers will wait in queue
            // Originate 3 calls sequentially so they queue in order
            for (var i = 0; i < 3; i++)
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/500@test-functional",
                    Context = "test-functional",
                    Exten = "500",
                    Priority = 1,
                    IsAsync = true,
                    Timeout = 15000,
                    ActionId = $"multi-queue-{i:D2}"
                });
                // Brief delay to ensure ordering
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            // Wait for all 3 callers to join
            var allJoined = await Task.WhenAny(allJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            allJoined.Should().Be(allJoinedTcs.Task, "all 3 callers should join the queue");

            // Verify positions are increasing (1, 2, 3 or similar)
            lock (positionLock)
            {
                positions.Should().HaveCount(3);
                positions.Should().BeInAscendingOrder("queue positions should increase as callers join");
            }
        }
        finally
        {
            // Cleanup: hangup all channels
            try
            {
                await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });
            }
            catch { /* best effort */ }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    [AsteriskContainerFact]
    public async Task QueueTimeout_ShouldProduceAbandonEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var abandonTcs = new TaskCompletionSource<QueueCallerAbandonEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Subscribe(new AbandonObserver(abandonTcs));

        // No members in queue — caller will wait, then we force hangup to trigger abandon
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Wait for call to enter queue, then hangup to force abandon
        await Task.Delay(TimeSpan.FromSeconds(2));
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

        // Wait for the abandon event
        var result = await Task.WhenAny(abandonTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(abandonTcs.Task, "QueueCallerAbandonEvent should fire after caller hangs up from empty queue");

        var abandonEvent = await abandonTcs.Task;
        abandonEvent.Should().NotBeNull();
    }

    [AsteriskContainerFact]
    public async Task AgentAndQueueManager_ShouldCorrelateCallFlow()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        var callerJoinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Queues.CallerJoined += (queueName, _) =>
        {
            if (string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                callerJoinedTcs.TrySetResult(true);
        };

        try
        {
            // Add a member so the queue has an agent
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Originate a call to the queue
            await connection.SendActionAsync(new OriginateAction
            {
                Channel = "Local/500@test-functional",
                Context = "test-functional",
                Exten = "500",
                Priority = 1,
                IsAsync = true,
                Timeout = 10000
            });

            // Wait for caller to join
            var joinResult = await Task.WhenAny(callerJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            joinResult.Should().Be(callerJoinedTcs.Task, "caller should join queue");

            // Verify queue manager has the queue with a member
            var queue = server.Queues.GetByName(TestQueue);
            queue.Should().NotBeNull("queue should exist in QueueManager");
            queue!.Members.Should().ContainKey(TestInterface, "member should be in queue");

            // Verify queue has at least one entry (the caller)
            queue.Entries.Should().NotBeEmpty("queue should have the caller entry");

            // Verify channel manager shows active channels
            server.Channels.ChannelCount.Should().BeGreaterThan(0,
                "there should be active channels from the originate");
        }
        finally
        {
            try
            {
                await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });
            }
            catch { /* best effort */ }
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                await connection.SendActionAsync(new QueueRemoveAction
                {
                    Queue = TestQueue,
                    Interface = TestInterface
                });
            }
            catch { /* best effort */ }
        }
    }

    [AsteriskContainerFact]
    public async Task QueueWithNoMembers_ShouldHandleGracefully()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Originate call to queue with no members — should not crash
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Wait for the call to enter and exit naturally
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Hangup remaining channels
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Connection should still be healthy after the call
        connection.State.Should().Be(AmiConnectionState.Connected,
            "AMI connection should remain healthy after queue call with no members");

        // Verify we can still send actions
        var response = await connection.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success", "connection should still accept actions");
    }

    /// <summary>Observer that captures QueueCallerAbandonEvent from the AMI event stream.</summary>
    private sealed class AbandonObserver(TaskCompletionSource<QueueCallerAbandonEvent> tcs)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is QueueCallerAbandonEvent abandon)
                tcs.TrySetResult(abandon);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
