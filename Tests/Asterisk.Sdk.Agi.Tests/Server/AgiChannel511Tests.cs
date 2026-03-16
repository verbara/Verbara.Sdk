using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Agi.Server;
using FluentAssertions;

namespace Asterisk.Sdk.Agi.Tests.Server;

public class AgiChannel511Tests
{
    [Fact]
    public async Task SendCommand_ShouldThrowAgiHangupException_WhenStatus511()
    {
        var cmdPipe = new Pipe();   // channel -> asterisk (commands)
        var replyPipe = new Pipe(); // asterisk -> channel (replies)

        var writer = new FastAgiWriter(cmdPipe.Writer);
        var reader = new FastAgiReader(replyPipe.Reader);
        var channel = new AgiChannel(writer, reader);

        // Simulate Asterisk returning status 511
        await replyPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("511 result=-1\n"));
        await replyPipe.Writer.CompleteAsync();

        var act = async () => await channel.SendCommandAsync("ANSWER");

        await act.Should().ThrowAsync<AgiHangupException>()
            .WithMessage("*511*");
    }

    [Fact]
    public async Task SendCommand_ShouldNotThrow_WhenStatus200()
    {
        var cmdPipe = new Pipe();
        var replyPipe = new Pipe();

        var writer = new FastAgiWriter(cmdPipe.Writer);
        var reader = new FastAgiReader(replyPipe.Reader);
        var channel = new AgiChannel(writer, reader);

        await replyPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("200 result=0\n"));
        await replyPipe.Writer.CompleteAsync();

        var reply = await channel.SendCommandAsync("ANSWER");

        reply.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendCommand_ShouldThrowAgiHangupException_WhenStatus511_WithCommandObject()
    {
        var cmdPipe = new Pipe();
        var replyPipe = new Pipe();

        var writer = new FastAgiWriter(cmdPipe.Writer);
        var reader = new FastAgiReader(replyPipe.Reader);
        var channel = new AgiChannel(writer, reader);

        await replyPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("511 result=-1\n"));
        await replyPipe.Writer.CompleteAsync();

        var act = async () => await channel.SendCommandAsync(new Asterisk.Sdk.Agi.Commands.AnswerCommand());

        await act.Should().ThrowAsync<AgiHangupException>();
    }
}
