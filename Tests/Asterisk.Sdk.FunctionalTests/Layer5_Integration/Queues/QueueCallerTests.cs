namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Queues;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class QueueCallerTests : FunctionalTestBase
{
    private const string TestQueue = "test-queue";

    public QueueCallerTests() : base("Asterisk.Sdk.Live")
    {
    }

    [AsteriskContainerFact]
    public async Task CallerJoin_ShouldAddEntryViaRawFields()
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

        // Originate a call to extension 500 (Queue test-queue) with no members — caller will wait
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 10000
        });

        // Wait for the caller to join the queue
        var joined = await Task.WhenAny(callerJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        joined.Should().Be(callerJoinedTcs.Task, "caller should join the queue within timeout");

        // Verify the entry is tracked in QueueManager
        var queue = server.Queues.GetByName(TestQueue);
        queue.Should().NotBeNull();
        queue!.Entries.Should().NotBeEmpty("queue should have at least one caller entry");
    }

    [AsteriskContainerFact]
    public async Task CallerLeave_ShouldRecordWaitTimeMetric()
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
        var callerLeftTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.Queues.CallerJoined += (queueName, _) =>
        {
            if (string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                callerJoinedTcs.TrySetResult(true);
        };
        server.Queues.CallerLeft += (queueName, _) =>
        {
            if (string.Equals(queueName, TestQueue, StringComparison.OrdinalIgnoreCase))
                callerLeftTcs.TrySetResult(true);
        };

        // Originate call to queue — it will timeout and leave
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Wait for join
        var joinTask = await Task.WhenAny(callerJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        joinTask.Should().Be(callerJoinedTcs.Task, "caller should join within timeout");

        // Hangup via CLI to trigger leave
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

        // Wait for leave event
        var leftTask = await Task.WhenAny(callerLeftTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        leftTask.Should().Be(callerLeftTcs.Task, "caller should leave the queue after hangup");

        // Verify metrics captured the wait time
        var callsLeft = MetricsCapture.Get("live.queue.calls.left");
        callsLeft.Should().BeGreaterThanOrEqualTo(1, "at least one call should have left the queue");
    }

    [AsteriskContainerFact]
    public async Task CallerAbandon_ShouldFireEvent()
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

        // Subscribe directly to AMI events — QueueCallerAbandon is not consumed by QueueManager
        connection.Subscribe(new AbandonObserver(abandonTcs));

        // Originate call to queue with no members — caller will eventually abandon
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        // Wait briefly for the call to enter the queue, then hangup to trigger abandon
        await Task.Delay(TimeSpan.FromSeconds(2));
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

        // Wait for the abandon event
        var result = await Task.WhenAny(abandonTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(abandonTcs.Task, "QueueCallerAbandonEvent should fire after caller hangs up");

        var abandonEvent = await abandonTcs.Task;
        abandonEvent.Should().NotBeNull();
    }

    [AsteriskContainerFact]
    public async Task QueueStatus_ShouldRebuildFullState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        const string testInterface = "Local/100@test-functional";

        try
        {
            // Add a member to test-queue
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = testInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Clear QueueManager state to simulate reconnect
            server.Queues.Clear();
            server.Queues.GetByName(TestQueue).Should().BeNull("state should be cleared");

            // Rebuild state via QueueStatusAction
            await foreach (var evt in connection.SendEventGeneratingActionAsync(new QueueStatusAction()))
            {
                switch (evt)
                {
                    case QueueParamsEvent qp:
                        server.Queues.OnQueueParams(
                            qp.Queue ?? "", qp.Max ?? 0, qp.Strategy,
                            qp.Calls ?? 0, qp.HoldTime ?? 0, qp.TalkTime ?? 0,
                            qp.Completed ?? 0, qp.Abandoned ?? 0);
                        break;
                    case QueueMemberEvent qm:
                        server.Queues.OnMemberAdded(
                            qm.Queue ?? "", qm.Location ?? qm.Interface ?? "", qm.MemberName,
                            qm.Penalty ?? 0, qm.Paused ?? false, qm.Status ?? 0);
                        break;
                    case QueueEntryEvent qe:
                        server.Queues.OnCallerJoined(
                            qe.Queue ?? "", qe.Channel ?? "", qe.CallerId, qe.Position ?? 0);
                        break;
                }
            }

            // Verify state was rebuilt
            var queue = server.Queues.GetByName(TestQueue);
            queue.Should().NotBeNull("test-queue should exist after QueueStatus rebuild");
            queue!.Members.Should().ContainKey(testInterface,
                "member should be present after QueueStatus rebuild");
        }
        finally
        {
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = testInterface
            });
        }
    }

    [AsteriskContainerFact]
    public async Task QueueSummary_ShouldReturnAccurateStats()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        const string testInterface = "Local/100@test-functional";

        try
        {
            // Add a member so LoggedIn > 0
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = testInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Send QueueSummaryAction and collect events
            QueueSummaryEvent? summaryEvent = null;
            await foreach (var evt in connection.SendEventGeneratingActionAsync(
                               new QueueSummaryAction { Queue = TestQueue }))
            {
                if (evt is QueueSummaryEvent qse && string.Equals(qse.Queue, TestQueue, StringComparison.OrdinalIgnoreCase))
                {
                    summaryEvent = qse;
                }
            }

            summaryEvent.Should().NotBeNull("QueueSummaryEvent should be received for test-queue");
            summaryEvent!.LoggedIn.Should().BeGreaterThanOrEqualTo(1,
                "at least one member should be logged in");
            summaryEvent.Available.Should().BeGreaterThanOrEqualTo(0);
            summaryEvent.Callers.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            await connection.SendActionAsync(new QueueRemoveAction
            {
                Queue = TestQueue,
                Interface = testInterface
            });
        }
    }

    /// <summary>Observer that captures QueueCallerAbandonEvent from the AMI event stream.</summary>
    private sealed class AbandonObserver(TaskCompletionSource<QueueCallerAbandonEvent> tcs) : IObserver<ManagerEvent>
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
