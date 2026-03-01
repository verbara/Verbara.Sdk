using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Resources;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AriJsonBenchmark
{
    private string _channelJson = null!;
    private string _bridgeJson = null!;
    private string _channelArrayJson = null!;
    private AriChannel _channel = null!;
    private AriBridge _bridge = null!;

    [GlobalSetup]
    public void Setup()
    {
        _channel = new AriChannel { Id = "1234567890.1", Name = "PJSIP/2000-00000001", State = AriChannelState.Up };
        _bridge = new AriBridge
        {
            Id = "bridge-1",
            Technology = "simple_bridge",
            BridgeType = "mixing",
            Channels = ["1234567890.1", "1234567890.2"]
        };

        _channelJson = JsonSerializer.Serialize(_channel, AriJsonContext.Default.AriChannel);
        _bridgeJson = JsonSerializer.Serialize(_bridge, AriJsonContext.Default.AriBridge);

        var channels = Enumerable.Range(0, 100).Select(i =>
            new AriChannel { Id = $"{i}.1", Name = $"PJSIP/200{i}-{i:D8}", State = AriChannelState.Up }).ToArray();
        _channelArrayJson = JsonSerializer.Serialize(channels, AriJsonContext.Default.AriChannelArray);
    }

    [Benchmark]
    public AriChannel? DeserializeChannel()
    {
        return JsonSerializer.Deserialize(_channelJson, AriJsonContext.Default.AriChannel);
    }

    [Benchmark]
    public string SerializeChannel()
    {
        return JsonSerializer.Serialize(_channel, AriJsonContext.Default.AriChannel);
    }

    [Benchmark]
    public AriBridge? DeserializeBridge()
    {
        return JsonSerializer.Deserialize(_bridgeJson, AriJsonContext.Default.AriBridge);
    }

    [Benchmark]
    public string SerializeBridge()
    {
        return JsonSerializer.Serialize(_bridge, AriJsonContext.Default.AriBridge);
    }

    [Benchmark]
    public AriChannel[]? Deserialize100Channels()
    {
        return JsonSerializer.Deserialize(_channelArrayJson, AriJsonContext.Default.AriChannelArray);
    }
}
