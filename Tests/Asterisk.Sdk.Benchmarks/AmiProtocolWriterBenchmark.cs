using System.IO.Pipelines;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AmiProtocolWriterBenchmark
{
    private Pipe _pipe = null!;
    private AmiProtocolWriter _writer = null!;
    private KeyValuePair<string, string>[] _originateFields = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pipe = new Pipe();
        _writer = new AmiProtocolWriter(_pipe.Writer);
        _originateFields =
        [
            new("Channel", "SIP/2000"),
            new("Context", "default"),
            new("Exten", "100"),
            new("Priority", "1"),
            new("CallerId", "Test <1234>"),
            new("Timeout", "30000"),
            new("Async", "true")
        ];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
    }

    [Benchmark(Baseline = true)]
    public async Task WriteSimpleAction()
    {
        await _writer.WriteActionAsync("Ping", "test-1");
        // Drain pipe to prevent backpressure
        _pipe.Reader.TryRead(out var result);
        _pipe.Reader.AdvanceTo(result.Buffer.End);
    }

    [Benchmark]
    public async Task WriteActionWithFields()
    {
        await _writer.WriteActionAsync("Originate", "test-2", _originateFields);
        _pipe.Reader.TryRead(out var result);
        _pipe.Reader.AdvanceTo(result.Buffer.End);
    }

    [Benchmark]
    public async Task Write1000Actions()
    {
        for (int i = 0; i < 1000; i++)
        {
            await _writer.WriteActionAsync("Ping", "action-0");
        }
        _pipe.Reader.TryRead(out var result);
        _pipe.Reader.AdvanceTo(result.Buffer.End);
    }
}
