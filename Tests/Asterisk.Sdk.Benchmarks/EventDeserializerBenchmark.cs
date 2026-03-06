using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the full event pipeline: wire bytes -> AmiProtocolReader -> AmiMessage -> parse.
/// This measures the real-world hot path from TCP bytes to typed ManagerEvent.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EventDeserializerBenchmark
{
    private byte[] _newchannelBytes = null!;
    private byte[] _varsetBytes = null!;
    private byte[] _queueParamsBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _newchannelBytes = Encoding.UTF8.GetBytes(
            "Event: Newchannel\r\n" +
            "Channel: SIP/2000-00000001\r\n" +
            "ChannelState: 0\r\n" +
            "ChannelStateDesc: Down\r\n" +
            "CallerIDNum: 2000\r\n" +
            "CallerIDName: Test User\r\n" +
            "ConnectedLineNum: \r\n" +
            "ConnectedLineName: \r\n" +
            "Language: en\r\n" +
            "AccountCode: \r\n" +
            "Context: default\r\n" +
            "Exten: 100\r\n" +
            "Priority: 1\r\n" +
            "Uniqueid: 1234567890.1\r\n" +
            "Linkedid: 1234567890.1\r\n\r\n");

        _varsetBytes = Encoding.UTF8.GetBytes(
            "Event: VarSet\r\n" +
            "Channel: SIP/2000-00000001\r\n" +
            "Variable: CDR(src)\r\n" +
            "Value: 2000\r\n" +
            "Uniqueid: 1234567890.1\r\n\r\n");

        _queueParamsBytes = Encoding.UTF8.GetBytes(
            "Event: QueueParams\r\n" +
            "Queue: sales\r\n" +
            "Max: 0\r\n" +
            "Strategy: ringall\r\n" +
            "Calls: 5\r\n" +
            "Holdtime: 30\r\n" +
            "TalkTime: 120\r\n" +
            "Completed: 42\r\n" +
            "Abandoned: 3\r\n" +
            "ServiceLevel: 60\r\n" +
            "ServicelevelPerf: 85.5\r\n" +
            "Weight: 0\r\n\r\n");
    }

    [Benchmark(Baseline = true)]
    public async Task<AmiMessage?> ParseNewchannel()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_newchannelBytes);
        await pipe.Writer.CompleteAsync();
        return await reader.ReadMessageAsync();
    }

    [Benchmark]
    public async Task<AmiMessage?> ParseVarSet()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_varsetBytes);
        await pipe.Writer.CompleteAsync();
        return await reader.ReadMessageAsync();
    }

    [Benchmark]
    public async Task<AmiMessage?> ParseQueueParams()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_queueParamsBytes);
        await pipe.Writer.CompleteAsync();
        return await reader.ReadMessageAsync();
    }
}
