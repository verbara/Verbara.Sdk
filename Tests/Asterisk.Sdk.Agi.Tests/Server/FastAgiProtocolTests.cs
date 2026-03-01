using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Agi.Server;
using FluentAssertions;

namespace Asterisk.Sdk.Agi.Tests.Server;

public class FastAgiProtocolTests
{
    [Fact]
    public async Task Reader_ShouldReadRequest()
    {
        var pipe = new Pipe();
        var reader = new FastAgiReader(pipe.Reader);

        var data = "agi_network: yes\n"
            + "agi_network_script: Hello\n"
            + "agi_channel: SIP/2000\n"
            + "agi_uniqueid: 123.1\n"
            + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        var request = await reader.ReadRequestAsync();

        request.IsNetwork.Should().BeTrue();
        request.Script.Should().Be("Hello");
        request.Channel.Should().Be("SIP/2000");
    }

    [Fact]
    public async Task Reader_ShouldReadReply()
    {
        var pipe = new Pipe();
        var reader = new FastAgiReader(pipe.Reader);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("200 result=0\n"));
        await pipe.Writer.CompleteAsync();

        var reply = await reader.ReadReplyAsync();

        reply.Should().NotBeNull();
        reply!.IsSuccess.Should().BeTrue();
        reply.Result.Should().Be("0");
    }

    [Fact]
    public async Task Writer_ShouldSendCommand()
    {
        var pipe = new Pipe();
        var writer = new FastAgiWriter(pipe.Writer);

        await writer.SendCommandAsync("ANSWER");
        await pipe.Writer.CompleteAsync();

        var result = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        text.Should().Be("ANSWER\n");
    }

    [Fact]
    public async Task RoundTrip_WriteCommandReadReply()
    {
        // Simulate: client sends command, server replies
        var cmdPipe = new Pipe();  // client -> server
        var replyPipe = new Pipe(); // server -> client

        var writer = new FastAgiWriter(cmdPipe.Writer);
        var reader = new FastAgiReader(replyPipe.Reader);

        // Client sends command
        await writer.SendCommandAsync("STREAM FILE welcome \"\"");

        // Server reads command
        var cmdResult = await cmdPipe.Reader.ReadAsync();
        var cmdText = Encoding.UTF8.GetString(cmdResult.Buffer.FirstSpan);
        cmdText.Should().Be("STREAM FILE welcome \"\"\n");
        cmdPipe.Reader.AdvanceTo(cmdResult.Buffer.End);

        // Server sends reply
        await replyPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("200 result=0\n"));
        await replyPipe.Writer.CompleteAsync();

        // Client reads reply
        var reply = await reader.ReadReplyAsync();
        reply!.IsSuccess.Should().BeTrue();

        await cmdPipe.Writer.CompleteAsync();
    }
}
