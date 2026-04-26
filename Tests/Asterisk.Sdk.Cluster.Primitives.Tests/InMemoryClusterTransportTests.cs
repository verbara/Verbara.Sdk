using Asterisk.Sdk.Cluster.Primitives;
using Asterisk.Sdk.Cluster.Primitives.InMemory;
using FluentAssertions;
using Xunit;

namespace Asterisk.Sdk.Cluster.Primitives.Tests;

public sealed class InMemoryClusterTransportTests
{
    private sealed record TestEvent(string SourceInstanceId, DateTimeOffset Timestamp, string Tag)
        : ClusterEvent(SourceInstanceId, Timestamp);

    private static async Task WaitForSubscribersAsync(
        InMemoryClusterTransport transport, int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (transport.SubscriberCount < expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Expected {expected} subscriber(s); saw {transport.SubscriberCount}");
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task PublishAsync_ShouldDeliverToSingleSubscriber()
    {
        await using var transport = new InMemoryClusterTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<ClusterEvent>();
        var task = Task.Run(async () =>
        {
            await foreach (var evt in transport.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                received.Add(evt);
                if (received.Count == 1) return;
            }
        });

        await WaitForSubscribersAsync(transport, 1, cts.Token);

        var sent = new TestEvent("instance-A", DateTimeOffset.UtcNow, "ping");
        await transport.PublishAsync(sent, cts.Token);

        await task.WaitAsync(TimeSpan.FromSeconds(2));

        received.Should().ContainSingle();
        received[0].Should().BeEquivalentTo(sent);
    }

    [Fact]
    public async Task PublishAsync_ShouldFanOutToMultipleSubscribers()
    {
        await using var transport = new InMemoryClusterTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var countA = 0;
        var countB = 0;
        var tcsA = new TaskCompletionSource();
        var tcsB = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            await foreach (var _ in transport.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                countA++;
                tcsA.TrySetResult();
            }
        });
        _ = Task.Run(async () =>
        {
            await foreach (var _ in transport.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                countB++;
                tcsB.TrySetResult();
            }
        });

        await WaitForSubscribersAsync(transport, 2, cts.Token);

        await transport.PublishAsync(new TestEvent("i", DateTimeOffset.UtcNow, "fan"), cts.Token);

        await Task.WhenAll(tcsA.Task, tcsB.Task).WaitAsync(TimeSpan.FromSeconds(2));

        countA.Should().Be(1);
        countB.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_ShouldPreserveTraceContext()
    {
        await using var transport = new InMemoryClusterTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        ClusterEvent? received = null;
        var tcs = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await foreach (var evt in transport.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                received = evt;
                tcs.TrySetResult();
                return;
            }
        });

        await WaitForSubscribersAsync(transport, 1, cts.Token);

        var sent = new TestEvent("i", DateTimeOffset.UtcNow, "trace")
        {
            TraceContext = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };
        await transport.PublishAsync(sent, cts.Token);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        received.Should().NotBeNull();
        received!.TraceContext.Should().Be(sent.TraceContext);
    }

    [Fact]
    public async Task SubscribeAsync_ShouldRemoveChannelOnCancellation()
    {
        await using var transport = new InMemoryClusterTransport();

        using var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in transport.SubscribeAsync(cts.Token).ConfigureAwait(false)) { }
            }
            catch (OperationCanceledException) { }
        });

        await WaitForSubscribersAsync(transport, 1, CancellationToken.None);
        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));

        // After cancellation, publishing doesn't throw and delivery is silent (no subscribers).
        await transport.PublishAsync(new TestEvent("i", DateTimeOffset.UtcNow, "post-cancel"));
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteActiveSubscribers()
    {
        var transport = new InMemoryClusterTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var task = Task.Run(async () =>
        {
            await foreach (var _ in transport.SubscribeAsync(cts.Token).ConfigureAwait(false)) { }
        });

        await WaitForSubscribersAsync(transport, 1, cts.Token);
        await transport.DisposeAsync();

        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
