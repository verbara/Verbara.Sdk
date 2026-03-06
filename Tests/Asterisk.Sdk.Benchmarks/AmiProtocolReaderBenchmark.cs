using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AmiProtocolReaderBenchmark
{
    private byte[] _singleEventBytes = null!;
    private byte[] _batchEventsBytes = null!;
    private byte[] _responseBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _singleEventBytes = Encoding.UTF8.GetBytes(
            "Event: Newchannel\r\nChannel: SIP/2000-00000001\r\nUniqueid: 123.1\r\n" +
            "CallerIDNum: 2000\r\nCallerIDName: Test\r\nContext: default\r\n" +
            "Exten: 100\r\nPriority: 1\r\nPrivilege: call,all\r\n\r\n");

        _responseBytes = Encoding.UTF8.GetBytes(
            "Response: Success\r\nActionID: test-1\r\nMessage: Pong\r\n" +
            "Ping: Pong\r\nTimestamp: 1234567890.000\r\n\r\n");

        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"Event: Newchannel\r\nChannel: SIP/2000-{i:D8}\r\nUniqueid: {i}.1\r\n" +
                $"CallerIDNum: 2000\r\nContext: default\r\nExten: 100\r\n\r\n");
        }
        _batchEventsBytes = Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<AmiMessage?> ParseBytes(byte[] data)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        pipe.Writer.Complete();
        var reader = new AmiProtocolReader(pipe.Reader);
        var result = await reader.ReadMessageAsync();
        pipe.Reader.Complete();
        return result;
    }

    [Benchmark(Baseline = true)]
    public Task<AmiMessage?> ParseSingleEvent() => ParseBytes(_singleEventBytes);

    [Benchmark]
    public Task<AmiMessage?> ParseResponse() => ParseBytes(_responseBytes);

    [Benchmark]
    public async Task<int> Parse100EventBatch()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(_batchEventsBytes);
        pipe.Writer.Complete();
        var reader = new AmiProtocolReader(pipe.Reader);
        int count = 0;
        while (await reader.ReadMessageAsync() is not null) { count++; }
        pipe.Reader.Complete();
        return count;
    }
}
