using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Events;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AriParseEventBenchmark
{
    private string _stasisStartJson = null!;
    private string _channelDtmfJson = null!;
    private string _unknownEventJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stasisStartJson = """
        {
            "type": "StasisStart",
            "timestamp": "2026-03-05T10:30:00.000+0000",
            "application": "myapp",
            "args": ["arg1", "arg2"],
            "channel": {
                "id": "1234567890.1",
                "name": "PJSIP/2000-00000001",
                "state": "Ring",
                "caller": { "name": "Alice", "number": "2000" },
                "connected": { "name": "", "number": "" },
                "accountcode": "",
                "dialplan": { "context": "default", "exten": "100", "priority": 1 },
                "creationtime": "2026-03-05T10:30:00.000+0000",
                "language": "en"
            }
        }
        """;

        _channelDtmfJson = """
        {
            "type": "ChannelDtmfReceived",
            "timestamp": "2026-03-05T10:30:01.000+0000",
            "application": "myapp",
            "digit": "5",
            "duration_ms": 100,
            "channel": {
                "id": "1234567890.1",
                "name": "PJSIP/2000-00000001",
                "state": "Up"
            }
        }
        """;

        _unknownEventJson = """
        {
            "type": "SomeUnknownFutureEvent",
            "timestamp": "2026-03-05T10:30:02.000+0000",
            "application": "myapp"
        }
        """;
    }

    [Benchmark(Baseline = true)]
    public AriEvent? ParseStasisStart()
    {
        return AriClient.ParseEvent(_stasisStartJson);
    }

    [Benchmark]
    public AriEvent? ParseChannelDtmf()
    {
        return AriClient.ParseEvent(_channelDtmfJson);
    }

    [Benchmark]
    public AriEvent? ParseUnknownEvent()
    {
        return AriClient.ParseEvent(_unknownEventJson);
    }
}
