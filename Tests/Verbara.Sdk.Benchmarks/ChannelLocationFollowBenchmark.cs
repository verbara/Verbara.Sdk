using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the cost of following a channel through the dialplan: <c>ChannelManager.OnNewExten</c> on 1,000 held
/// channels with no subscribers, and a read of <c>AsteriskChannel.ExtensionHistory</c>, which returns a copy.
/// <list type="bullet">
/// <item><c>NewExten5PerChannel_Fresh</c>: 5 reports per channel on a table rebuilt before each iteration, so the
/// history grows from empty (the first steps of a call). Divide by 5,000 for the per-report figure.</item>
/// <item><c>NewExten5PerChannel_AtHistoryBound</c>: the same 5,000 reports on channels whose history already holds
/// the 100-entry bound, the steady state of a long call.</item>
/// <item><c>NewExten5PerChannel_CapturingLambdaControl</c>: the fresh row plus one capturing closure per report, the
/// negative control that shows the bytes column moves when a report allocates more.</item>
/// <item><c>ReadExtensionHistory_AtBound_1000Channels</c>: one history read per channel at the bound.</item>
/// </list>
/// This class has no counterpart on a tag before <c>OnNewExten</c> existed, so its rows read as absolute numbers.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ChannelLocationFollowBenchmark
{
    private const int Channels = 1_000;
    private const int ReportsPerChannel = 5;
    private const int HistoryBound = 100;

    private static readonly string[] Extens = ["s", "100", "200", "300", "400"];

    private string[] _ids = null!;
    private ChannelManager _fresh = null!;
    private ChannelManager _atBound = null!;
    private AsteriskChannel[] _atBoundChannels = null!;
    private int _sink;

    [GlobalSetup]
    public void Setup()
    {
        _ids = new string[Channels];
        for (int i = 0; i < Channels; i++)
            _ids[i] = $"{i}.1";

        _atBound = Build();
        for (int round = 0; round < HistoryBound; round++)
        {
            for (int i = 0; i < Channels; i++)
                _atBound.OnNewExten(_ids[i], "default", Extens[round % Extens.Length], round + 1);
        }

        _atBoundChannels = new AsteriskChannel[Channels];
        for (int i = 0; i < Channels; i++)
            _atBoundChannels[i] = _atBound.GetByUniqueId(_ids[i])!;
    }

    [IterationSetup(Targets = [nameof(NewExten5PerChannel_Fresh), nameof(NewExten5PerChannel_CapturingLambdaControl)])]
    public void RebuildFreshTable() => _fresh = Build();

    private ChannelManager Build()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        for (int i = 0; i < Channels; i++)
            manager.OnNewChannel(_ids[i], $"PJSIP/{2000 + i}-{i:D8}", ChannelState.Up, context: "default", exten: "s");
        return manager;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Channels * ReportsPerChannel)]
    public void NewExten5PerChannel_Fresh()
    {
        for (int step = 0; step < ReportsPerChannel; step++)
        {
            for (int i = 0; i < Channels; i++)
                _fresh.OnNewExten(_ids[i], "default", Extens[step], step + 1);
        }
    }

    [Benchmark(OperationsPerInvoke = Channels * ReportsPerChannel)]
    public void NewExten5PerChannel_AtHistoryBound()
    {
        for (int step = 0; step < ReportsPerChannel; step++)
        {
            for (int i = 0; i < Channels; i++)
                _atBound.OnNewExten(_ids[i], "default", Extens[step], step + 1);
        }
    }

    [Benchmark(OperationsPerInvoke = Channels * ReportsPerChannel)]
    public int NewExten5PerChannel_CapturingLambdaControl()
    {
        for (int step = 0; step < ReportsPerChannel; step++)
        {
            for (int i = 0; i < Channels; i++)
            {
                _fresh.OnNewExten(_ids[i], "default", Extens[step], step + 1);
                int captured = i;
                Func<int> closure = () => captured + _sink;
                _sink = closure();
            }
        }
        return _sink;
    }

    [Benchmark(OperationsPerInvoke = Channels)]
    public int ReadExtensionHistory_AtBound_1000Channels()
    {
        int total = 0;
        for (int i = 0; i < Channels; i++)
            total += _atBoundChannels[i].ExtensionHistory.Count;
        return total;
    }
}
