using System.IO.Pipelines;
using Asterisk.NetAot.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.NetAot.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AmiProtocolWriterBenchmark
{
    private Pipe _pipe = null!;
    private AmiProtocolWriter _writer = null!;

    [IterationSetup]
    public void Setup()
    {
        _pipe = new Pipe();
        _writer = new AmiProtocolWriter(_pipe.Writer);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
    }

    [Benchmark]
    public async Task WriteSimpleAction()
    {
        await _writer.WriteActionAsync("Ping", "test-1");
    }

    [Benchmark]
    public async Task WriteActionWithFields()
    {
        await _writer.WriteActionAsync("Originate", "test-2",
        [
            new("Channel", "SIP/2000"),
            new("Context", "default"),
            new("Exten", "100"),
            new("Priority", "1"),
            new("CallerId", "Test <1234>"),
            new("Timeout", "30000"),
            new("Async", "true")
        ]);
    }

    [Benchmark]
    public async Task Write1000Actions()
    {
        for (int i = 0; i < 1000; i++)
        {
            await _writer.WriteActionAsync("Ping", $"action-{i}");
        }
    }
}
