namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Concurrency;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class ConcurrentActionTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    [AsteriskContainerFact]
    public async Task FiftyConcurrentPingActions_ShouldAllReceiveCorrectResponses()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        const int count = 50;
        var results = new ConcurrentBag<(string SentActionId, string? ReceivedActionId, string? Response)>();

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var action = new PingAction { ActionId = $"ping-{i:D4}" };
            var response = await connection.SendActionAsync(action);
            results.Add((action.ActionId!, response.ActionId, response.Response));
        });

        await Task.WhenAll(tasks);

        results.Should().HaveCount(count);
        foreach (var (sentId, receivedId, response) in results)
        {
            receivedId.Should().Be(sentId, "each response ActionId must match its request");
            response.Should().Be("Success");
        }
    }

    [AsteriskContainerFact]
    public async Task ConcurrentOriginateActions_ShouldAllSucceed()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        const int count = 10;
        var results = new ConcurrentBag<ManagerResponse>();

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var action = new OriginateAction
            {
                Channel = $"Local/s@default",
                Application = "Wait",
                Data = "1",
                IsAsync = true,
                ActionId = $"orig-{i:D4}"
            };
            var response = await connection.SendActionAsync(action);
            results.Add(response);
        });

        await Task.WhenAll(tasks);

        // All actions should get a response (Success or Error depending on dialplan),
        // but none should throw or be lost
        results.Should().HaveCount(count);
        foreach (var r in results)
        {
            r.Response.Should().NotBeNullOrEmpty("every originate must receive a response");
        }
    }

    [AsteriskContainerFact]
    public async Task ConcurrentSendAndSubscribe_ShouldNotDeadlock()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriptions = new ConcurrentBag<IDisposable>();

        // Task group 1: rapid subscribe/unsubscribe
        var subscribeTask = Task.Run(async () =>
        {
            for (var i = 0; i < 50 && !cts.IsCancellationRequested; i++)
            {
                var sub = connection.Subscribe(new NoOpObserver());
                subscriptions.Add(sub);
                await Task.Yield();
                sub.Dispose();
            }
        }, cts.Token);

        // Task group 2: send actions concurrently
        var sendTasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var response = await connection.SendActionAsync(new PingAction(), cts.Token);
            response.Response.Should().Be("Success");
        });

        // Must complete without deadlock within the timeout
        await Task.WhenAll(sendTasks.Append(subscribeTask));
    }

    [AsteriskContainerFact]
    public async Task RapidFireActions_ShouldNotCorruptState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        const int count = 100;
        var results = new ConcurrentBag<ManagerResponse>();

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var action = new PingAction { ActionId = $"rapid-{i:D4}" };
            var response = await connection.SendActionAsync(action);
            results.Add(response);
        });

        await Task.WhenAll(tasks);

        results.Should().HaveCount(count, "all 100 rapid-fire actions must receive responses");

        // Verify no duplicate ActionIds in responses (no cross-talk)
        var actionIds = results.Select(r => r.ActionId).Where(id => id is not null).ToList();
        actionIds.Should().OnlyHaveUniqueItems("response ActionIds must not be duplicated");
    }

    [AsteriskContainerFact]
    public async Task ConcurrentActionsWithTimeout_ShouldCleanupProperly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // Mix of normal pings and actions with very short per-action timeout
        var successCount = 0;
        var failureCount = 0;

        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            try
            {
                // Alternate between generous and very tight timeouts
                using var perActionCts = i % 5 == 0
                    ? new CancellationTokenSource(TimeSpan.FromMilliseconds(1))
                    : new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var response = await connection.SendActionAsync(new PingAction(), perActionCts.Token);
                if (response.Response == "Success")
                    Interlocked.Increment(ref successCount);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref failureCount);
            }
        });

        await Task.WhenAll(tasks);

        // At least some should succeed, some may timeout
        (successCount + failureCount).Should().Be(50, "every action must either succeed or timeout");

        // Verify connection is still healthy after timeouts
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success", "connection must remain functional after timeouts");
    }

    private sealed class NoOpObserver : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
