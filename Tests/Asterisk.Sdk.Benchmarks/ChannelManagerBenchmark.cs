using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Channels;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Benchmarks ChannelManager ConcurrentDictionary operations + per-entity Lock under load.
/// Simulates high-load scenarios with dual index (ByUniqueId + ByName).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ChannelManagerBenchmark
{
    private ChannelManager _sut = null!;
    private ChannelManager _preloaded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sut = new ChannelManager(NullLogger.Instance);

        // Preloaded manager with 10K channels for lookup/update benchmarks
        _preloaded = new ChannelManager(NullLogger.Instance);
        for (int i = 0; i < 10_000; i++)
        {
            _preloaded.OnNewChannel(
                $"{i}.1",
                $"PJSIP/{2000 + i}-{i:D8}",
                ChannelState.Up,
                callerIdNum: (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                context: "default",
                exten: "100");
        }
    }

    [Benchmark(Baseline = true)]
    public void Create1000Channels()
    {
        var mgr = new ChannelManager(NullLogger.Instance);
        for (int i = 0; i < 1000; i++)
        {
            mgr.OnNewChannel($"{i}.1", $"SIP/{i}-{i:D8}", ChannelState.Ring);
        }
    }

    [Benchmark]
    public void Update1000ChannelStates()
    {
        for (int i = 0; i < 1000; i++)
        {
            _preloaded.OnNewState($"{i}.1", ChannelState.Up);
        }
    }

    [Benchmark]
    public AsteriskChannel? LookupByUniqueId()
    {
        return _preloaded.GetByUniqueId("5000.1");
    }

    [Benchmark]
    public AsteriskChannel? LookupByName()
    {
        return _preloaded.GetByName("PJSIP/7000-00005000");
    }

    [Benchmark]
    public int EnumerateByState()
    {
        int count = 0;
        foreach (var ch in _preloaded.GetChannelsByState(ChannelState.Up))
            count++;
        return count;
    }
}
