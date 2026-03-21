namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.Soak;

using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

[Trait("Category", "Soak")]
public sealed class ChannelManagerSoakTests
{
    private readonly ChannelManager _manager = new(NullLogger<ChannelManager>.Instance);

    [Fact]
    public void CreateAndDestroyThousandChannels_ShouldReturnToZero()
    {
        const int count = 1_000;

        for (var i = 0; i < count; i++)
        {
            _manager.OnNewChannel(
                uniqueId: $"chan-{i}",
                channelName: $"Local/test-{i}@default;1",
                state: ChannelState.Ring);
        }

        _manager.ChannelCount.Should().Be(count);

        for (var i = 0; i < count; i++)
        {
            _manager.OnHangup($"chan-{i}", HangupCause.NormalClearing);
        }

        _manager.ChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentChannelOperations_ShouldMaintainConsistency()
    {
        const int taskCount = 10;
        const int channelsPerTask = 100;

        var tasks = Enumerable.Range(0, taskCount).Select(taskId => Task.Run(() =>
        {
            for (var i = 0; i < channelsPerTask; i++)
            {
                _manager.OnNewChannel(
                    uniqueId: $"chan-{taskId}-{i}",
                    channelName: $"Local/test-{taskId}-{i}@default;1",
                    state: ChannelState.Ring);
            }

            for (var i = 0; i < channelsPerTask; i++)
            {
                _manager.OnHangup($"chan-{taskId}-{i}", HangupCause.NormalClearing);
            }
        }));

        await Task.WhenAll(tasks);

        _manager.ChannelCount.Should().Be(0);
    }

    [Fact]
    public void RepeatedClearCycles_ShouldNotLeak()
    {
        const int cycles = 100;
        const int channelsPerCycle = 10;

        // Warm up GC and establish baseline after first cycle
        for (var i = 0; i < channelsPerCycle; i++)
        {
            _manager.OnNewChannel(
                uniqueId: $"warmup-{i}",
                channelName: $"Local/warmup-{i}@default;1",
                state: ChannelState.Ring);
        }

        _manager.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            for (var i = 0; i < channelsPerCycle; i++)
            {
                _manager.OnNewChannel(
                    uniqueId: $"chan-{cycle}-{i}",
                    channelName: $"Local/test-{cycle}-{i}@default;1",
                    state: ChannelState.Ring);
            }

            _manager.Clear();
            _manager.ChannelCount.Should().Be(0);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);

        var growth = finalMemory - baselineMemory;
        growth.Should().BeLessThan(5 * 1024 * 1024, "memory growth after 100 clear cycles should be under 5 MB");
    }
}
