using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ConcurrentThroughputBenchmark
{
    private byte[] _mixedMessages = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            // Alternate between events and responses
            if (i % 2 == 0)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"Event: Newchannel\r\nChannel: SIP/2000-{i:D8}\r\nUniqueid: {i}.1\r\n" +
                    $"CallerIDNum: 2000\r\nContext: default\r\n\r\n");
            }
            else
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"Response: Success\r\nActionID: action-{i}\r\nMessage: OK\r\n\r\n");
            }
        }
        _mixedMessages = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Benchmark(Description = "Parse 500 mixed messages (events + responses)")]
    public async Task<int> Parse500MixedMessages()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.WriteAsync(_mixedMessages);
        await pipe.Writer.CompleteAsync();

        int count = 0;
        while (await reader.ReadMessageAsync() is not null)
        {
            count++;
        }
        return count;
    }

    [Benchmark(Description = "Write + Read roundtrip 100 actions")]
    public async Task<int> WriteReadRoundtrip100()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var reader = new AmiProtocolReader(pipe.Reader);

        // Write 100 actions
        for (int i = 0; i < 100; i++)
        {
            await writer.WriteActionAsync("Ping", $"id-{i}");
        }
        await pipe.Writer.CompleteAsync();

        // Read them back
        int count = 0;
        while (await reader.ReadMessageAsync() is not null)
        {
            count++;
        }
        return count;
    }
}
