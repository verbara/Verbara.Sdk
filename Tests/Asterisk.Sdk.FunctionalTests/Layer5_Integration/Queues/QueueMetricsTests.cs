namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Queues;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests that verify LiveMetrics counters and histograms are recorded correctly
/// during queue call flows. Observable gauges (live.queues.count) are tested
/// via direct QueueManager assertions since MeterListener does not capture them.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class QueueMetricsTests : FunctionalTestBase
{
    private const string TestQueue = "test-queue";
    private const string TestInterface = "Local/100@test-functional";

    public QueueMetricsTests() : base("Asterisk.Sdk.Live")
    {
    }

    [Fact]
    public async Task CallerJoinLeave_ShouldIncrementCounters()
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

        // Originate call to queue with no members so it waits
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 10000
        });

        // Wait for join
        var joinResult = await Task.WhenAny(callerJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        joinResult.Should().Be(callerJoinedTcs.Task, "caller should join the queue");

        // Hangup to trigger leave
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

        // Wait for leave
        var leaveResult = await Task.WhenAny(callerLeftTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        leaveResult.Should().Be(callerLeftTcs.Task, "caller should leave the queue");

        // Verify counters
        MetricsCapture.Get("live.queue.calls.joined").Should()
            .BeGreaterThanOrEqualTo(1, "at least one call should have joined the queue");
        MetricsCapture.Get("live.queue.calls.left").Should()
            .BeGreaterThanOrEqualTo(1, "at least one call should have left the queue");
    }

    [Fact]
    public async Task WaitTime_ShouldRecordHistogram()
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

        // Originate call to queue — no members so caller waits
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/500@test-functional",
            Context = "test-functional",
            Exten = "500",
            Priority = 1,
            IsAsync = true,
            Timeout = 10000
        });

        // Wait for join
        var joinResult = await Task.WhenAny(callerJoinedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        joinResult.Should().Be(callerJoinedTcs.Task, "caller should join the queue");

        // Let the caller wait for a bit to accumulate wait time
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Hangup to trigger leave (which records the histogram)
        await connection.SendActionAsync(new CommandAction { Command = "channel request hangup all" });

        // Wait for leave
        var leaveResult = await Task.WhenAny(callerLeftTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        leaveResult.Should().Be(callerLeftTcs.Task, "caller should leave the queue");

        // The histogram live.queue.wait_time is recorded as a double in OnCallerLeft.
        // MetricsCapture casts double to long, so any wait > 0ms should register >= 1.
        MetricsCapture.Get("live.queue.wait_time").Should()
            .BeGreaterThan(0, "wait time histogram should record a positive value after caller waited in queue");
    }

    [Fact]
    public async Task QueueGauges_ShouldReflectCurrentState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        try
        {
            // Add a member so the queue is guaranteed to exist
            await connection.SendActionAsync(new QueueAddAction
            {
                Queue = TestQueue,
                Interface = TestInterface
            });
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Observable gauges are read-on-demand and not captured by MeterListener callbacks.
            // Instead, verify the underlying QueueManager state that the gauge reads from.
            server.Queues.QueueCount.Should().BeGreaterThan(0,
                "at least one queue should exist after adding a member");

            var queue = server.Queues.GetByName(TestQueue);
            queue.Should().NotBeNull("test-queue should be tracked by QueueManager");
            queue!.MemberCount.Should().BeGreaterThanOrEqualTo(1,
                "queue should have at least the member we added");
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
            catch { /* best effort */ }
        }
    }
}
