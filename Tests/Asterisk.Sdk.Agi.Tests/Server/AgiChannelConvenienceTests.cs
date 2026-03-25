using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Agi.Server;
using FluentAssertions;

namespace Asterisk.Sdk.Agi.Tests.Server;

public class AgiChannelConvenienceTests
{
    private static (AgiChannel Channel, Pipe CmdPipe, PipeWriter ReplyWriter) CreateChannel()
    {
        var cmdPipe = new Pipe();
        var replyPipe = new Pipe();
        var writer = new FastAgiWriter(cmdPipe.Writer);
        var reader = new FastAgiReader(replyPipe.Reader);
        var channel = new AgiChannel(writer, reader);
        return (channel, cmdPipe, replyPipe.Writer);
    }

    private static async Task WriteReply(PipeWriter writer, string reply)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"));
        await writer.CompleteAsync();
    }

    private static async Task<string> ReadSentCommand(Pipe cmdPipe)
    {
        await cmdPipe.Writer.CompleteAsync();
        var readResult = await cmdPipe.Reader.ReadAsync();
        return Encoding.UTF8.GetString(readResult.Buffer).TrimEnd('\n');
    }

    // ── AnswerAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task AnswerAsync_ShouldComplete_WhenReplyIsSuccess()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.AnswerAsync();

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("ANSWER");
    }

    [Fact]
    public async Task AnswerAsync_ShouldThrowAgiException_WhenReplyIsFailure()
    {
        var (channel, _, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "510 Invalid command");

        var act = async () => await channel.AnswerAsync();

        await act.Should().ThrowAsync<AgiException>()
            .WithMessage("ANSWER failed*");
    }

    // ── HangupAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task HangupAsync_ShouldSendHangupCommand()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=1");

        await channel.HangupAsync();

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("HANGUP");
    }

    // ── GetVariableAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetVariableAsync_ShouldReturnExtra_WhenPresent()
    {
        var (channel, _, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=1 (myValue)");

        var result = await channel.GetVariableAsync("MY_VAR");

        result.Should().Be("myValue");
    }

    [Fact]
    public async Task GetVariableAsync_ShouldReturnResult_WhenExtraIsNull()
    {
        var (channel, _, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        var result = await channel.GetVariableAsync("MISSING_VAR");

        result.Should().Be("0");
    }

    [Fact]
    public async Task GetVariableAsync_ShouldSendCorrectCommand()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.GetVariableAsync("CALLERID");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("GET VARIABLE CALLERID");
    }

    // ── SetVariableAsync ────────────────────────────────────────────

    [Fact]
    public async Task SetVariableAsync_ShouldSendCommandWithQuotedValue()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=1");

        await channel.SetVariableAsync("MY_VAR", "hello world");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("SET VARIABLE MY_VAR \"hello world\"");
    }

    // ── StreamFileAsync ─────────────────────────────────────────────

    [Fact]
    public async Task StreamFileAsync_ShouldSendEmptyEscapeDigits_WhenNoneProvided()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.StreamFileAsync("welcome");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("STREAM FILE welcome \"\"");
    }

    [Fact]
    public async Task StreamFileAsync_ShouldSendEscapeDigits_WhenProvided()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.StreamFileAsync("welcome", "0123456789#*");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("STREAM FILE welcome \"0123456789#*\"");
    }

    [Fact]
    public async Task StreamFileAsync_ShouldReturnResultAsChar_WhenDigitPressed()
    {
        var (channel, _, replyWriter) = CreateChannel();
        // ASCII 53 = '5'
        await WriteReply(replyWriter, "200 result=53");

        var result = await channel.StreamFileAsync("welcome", "0123456789");

        result.Should().Be('5');
    }

    [Fact]
    public async Task StreamFileAsync_ShouldReturnNullChar_WhenNoDigitPressed()
    {
        var (channel, _, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        var result = await channel.StreamFileAsync("welcome");

        result.Should().Be('0'); // result=0 → Result is "0" → ResultAsChar is '0'
    }

    // ── GetDataAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetDataAsync_ShouldSendFileOnly_WhenNoTimeoutOrMaxDigits()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=12345");

        var result = await channel.GetDataAsync("enter-pin");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("GET DATA enter-pin");
        result.Should().Be("12345");
    }

    [Fact]
    public async Task GetDataAsync_ShouldIncludeTimeout_WhenProvided()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=999");

        await channel.GetDataAsync("enter-pin", timeout: 5000);

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("GET DATA enter-pin 5000");
    }

    [Fact]
    public async Task GetDataAsync_ShouldIncludeTimeoutAndMaxDigits_WhenBothProvided()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=42");

        await channel.GetDataAsync("enter-pin", timeout: 3000, maxDigits: 4);

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("GET DATA enter-pin 3000 4");
    }

    // ── ExecAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecAsync_ShouldSendApplicationOnly_WhenArgsEmpty()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.ExecAsync("Playback");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("EXEC Playback");
    }

    [Fact]
    public async Task ExecAsync_ShouldSendApplicationWithArgs_WhenProvided()
    {
        var (channel, cmdPipe, replyWriter) = CreateChannel();
        await WriteReply(replyWriter, "200 result=0");

        await channel.ExecAsync("Dial", "SIP/100,30,tT");

        var sent = await ReadSentCommand(cmdPipe);
        sent.Should().Be("EXEC Dial SIP/100,30,tT");
    }

    // ── SendCommandAsync (null reply) ───────────────────────────────

    [Fact]
    public async Task SendCommandAsync_ShouldThrowAgiException_WhenConnectionClosed()
    {
        var (channel, _, replyWriter) = CreateChannel();
        // Complete without writing anything — simulates connection closed
        await replyWriter.CompleteAsync();

        var act = async () => await channel.SendCommandAsync("ANSWER");

        await act.Should().ThrowAsync<AgiException>()
            .WithMessage("*Connection closed*");
    }
}
