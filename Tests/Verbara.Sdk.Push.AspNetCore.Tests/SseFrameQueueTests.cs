namespace Verbara.Sdk.Push.AspNetCore.Tests;

using System.Text;
using Microsoft.Extensions.Options;

/// <summary>
/// The per-connection queue on its own: drop
/// oldest at a byte bound, the dropped event frames reported once before the next frame, heartbeats outside
/// the bound, and a frame larger than the bound still delivered alone.
/// </summary>
public sealed class SseFrameQueueTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly byte[] Heartbeat = SseFrameFormat.Heartbeat;

    [Fact]
    public async Task EnqueueEvent_ShouldDropOldestAndReportTheCountOnce_WhenTheBoundIsReached()
    {
        var queue = new SseFrameQueue(bound: 300);
        var results = Enumerable.Range(0, 5).Select(i => queue.EnqueueEvent(Frame(i, 100))).ToList();

        results.Select(static r => r.Dropped).Should().Equal([0, 0, 0, 1, 1], "the fourth and fifth frames each evict the oldest");
        results.Select(static r => r.EpisodeStarted).Should().Equal([false, false, false, true, false], "one episode until the drops are reported");
        queue.QueuedBytes.Should().Be(300);

        var first = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        first.Dropped.Should().Be(2, "the two dropped event frames are reported before the next frame");
        Sequence(first.Frame).Should().Be(2, "every frame after the gap is newer than every dropped one");

        var second = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        second.Dropped.Should().Be(0, "a drop is reported once");
        Sequence(second.Frame).Should().Be(3);
    }

    [Fact]
    public async Task TryEnqueueHeartbeat_ShouldNotQueue_WhileADropIsUnreported()
    {
        var queue = new SseFrameQueue(bound: 200);
        queue.EnqueueEvent(Frame(0, 100));
        queue.EnqueueEvent(Frame(1, 50));
        queue.TryEnqueueHeartbeat(Heartbeat).Should().BeTrue("a connection below the bound gets its heartbeats");
        queue.EnqueueEvent(Frame(2, 100)).Dropped.Should().Be(1, "the oldest event frame is dropped");

        queue.QueuedBytes.Should().Be(150, "the drop also removed the queued heartbeat, which is never counted");
        queue.TryEnqueueHeartbeat(Heartbeat).Should().BeFalse("a heartbeat is not queued while a drop is unreported");

        var next = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        next.Dropped.Should().Be(1, "heartbeats are not counted in the gap");
        Sequence(next.Frame).Should().Be(1, "the gap is followed by an event frame, never a heartbeat");
        queue.TryEnqueueHeartbeat(Heartbeat).Should().BeTrue("once the drop is reported and there is room, heartbeats resume");
    }

    [Fact]
    public void TryEnqueueHeartbeat_ShouldNotQueue_WhenItDoesNotFitUnderTheBound()
    {
        var queue = new SseFrameQueue(bound: 105);
        queue.EnqueueEvent(Frame(0, 100));

        queue.TryEnqueueHeartbeat(Heartbeat).Should().BeFalse("a heartbeat never takes the queue past the bound");
        queue.QueuedBytes.Should().Be(100);
    }

    [Fact]
    public async Task EnqueueEvent_ShouldQueueAFrameLargerThanTheBoundAlone_AndCountWhatItDisplaced()
    {
        var observed = new List<long>();
        var queue = new SseFrameQueue(bound: 100, observed.Add);
        queue.EnqueueEvent(Frame(0, 40));
        queue.EnqueueEvent(Frame(1, 40));

        var result = queue.EnqueueEvent(Frame(2, 500));

        result.Dropped.Should().Be(2, "everything queued before it is dropped and counted");
        result.EpisodeStarted.Should().BeTrue();
        observed.Should().Equal([40, 80, 500], "the hook sees the bytes queued after every frame; the large frame is queued alone");
        var next = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Bound);
        next.Dropped.Should().Be(2);
        next.Frame.Should().HaveCount(500, "the frame larger than the bound is still delivered, whole");
    }

    [Fact]
    public async Task DequeueAsync_ShouldWaitForAFrame_AndEndWhenCompleted()
    {
        var queue = new SseFrameQueue(bound: 1000);
        var pending = queue.DequeueAsync(CancellationToken.None).AsTask();
        pending.IsCompleted.Should().BeFalse("nothing is queued yet");

        queue.EnqueueEvent(Frame(7, 10));
        Sequence((await pending.WaitAsync(Bound)).Frame).Should().Be(7);

        var ending = queue.DequeueAsync(CancellationToken.None).AsTask();
        queue.Complete();
        (await ending.WaitAsync(Bound)).Frame.Should().BeNull("a completed queue ends its reader");
        queue.EnqueueEvent(Frame(8, 10)).Should().Be(default(SseEnqueueResult), "nothing is queued after completion");
        queue.QueuedBytes.Should().Be(0);
    }

    [Fact]
    public async Task DequeueAsync_ShouldThrowOperationCanceled_WhenTheConnectionEnds()
    {
        var queue = new SseFrameQueue(bound: 1000);
        using var cts = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => pending.WaitAsync(Bound)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Gap_ShouldBeOneGapEventWithTheDroppedCountAsJson()
    {
        Encoding.UTF8.GetString(SseFrameFormat.Gap(3413)).Should().Be("event: .gap\ndata: {\"dropped\":3413}\n\n");
    }

    [Theory]
    [InlineData("queue.a\r\ndata: x.updated", "queue.a%0D%0Adata: x.updated")]
    [InlineData(".gap", "%2Egap")]
    [InlineData("\n.gap", "%0A.gap")]
    [InlineData("queue.1.updated", "queue.1.updated")]
    public void EventName_ShouldNeverCarryALineBreakOrTheReservedGapName(string name, string expected)
    {
        SseFrameFormat.EventName(name).Should().Be(expected);
    }

    [Fact]
    public void Options_ShouldDefaultTheBoundTo1MiB()
    {
        new SsePushStreamOptions().MaxQueuedBytesPerConnection.Should().Be(1_048_576);
    }

    [Fact]
    public void Options_ShouldBeTheDefaults_WhenOnlyAddVerbaraPushIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPush();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOptions<SsePushStreamOptions>>().Value.MaxQueuedBytesPerConnection.Should().Be(1_048_576);
    }

    [Fact]
    public void Options_ShouldCarryTheHostsBound_WhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPushAspNetCore();
        services.Configure<SsePushStreamOptions>(static o => o.MaxQueuedBytesPerConnection = 4 * 1024 * 1024);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOptions<SsePushStreamOptions>>().Value.MaxQueuedBytesPerConnection.Should().Be(4 * 1024 * 1024);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Options_ShouldFailValidationAtStart_WhenTheBoundIsBelowOne(long bound)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraPushAspNetCore();
        services.Configure<SsePushStreamOptions>(o => o.MaxQueuedBytesPerConnection = bound);
        using var sp = services.BuildServiceProvider();

        var start = () => sp.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(nameof(SsePushStreamOptions.MaxQueuedBytesPerConnection));
    }

    private static byte[] Frame(int sequence, int size)
    {
        var frame = new byte[size];
        Array.Fill(frame, (byte)'x');
        frame[0] = (byte)sequence;
        return frame;
    }

    private static int Sequence(byte[]? frame) => frame is null ? -1 : frame[0];
}
