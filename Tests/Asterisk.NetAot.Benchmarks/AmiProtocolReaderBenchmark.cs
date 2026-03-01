using System.IO.Pipelines;
using System.Text;
using Asterisk.NetAot.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.NetAot.Benchmarks;

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
        for (int i = 0; i < 1000; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"Event: Newchannel\r\nChannel: SIP/2000-{i:D8}\r\nUniqueid: {i}.1\r\n" +
                $"CallerIDNum: 2000\r\nContext: default\r\nExten: 100\r\n\r\n");
        }
        _batchEventsBytes = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Benchmark]
    public async Task<AmiMessage?> ParseSingleEvent()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_singleEventBytes);
        await pipe.Writer.CompleteAsync();
        return await reader.ReadMessageAsync();
    }

    [Benchmark]
    public async Task<AmiMessage?> ParseResponse()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_responseBytes);
        await pipe.Writer.CompleteAsync();
        return await reader.ReadMessageAsync();
    }

    [Benchmark]
    public async Task<int> Parse1000Events()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_batchEventsBytes);
        await pipe.Writer.CompleteAsync();

        int count = 0;
        while (await reader.ReadMessageAsync() is not null)
        {
            count++;
        }
        return count;
    }
}
