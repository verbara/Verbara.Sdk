using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public class AmiProtocolReaderTests
{
    private static async Task<AmiMessage?> WriteAndRead(string data)
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        return await reader.ReadMessageAsync();
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseSimpleResponse()
    {
        var msg = await WriteAndRead("Response: Success\r\nActionID: 1\r\nMessage: Pong\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeTrue();
        msg.ResponseStatus.Should().Be("Success");
        msg.ActionId.Should().Be("1");
        msg["Message"].Should().Be("Pong");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseEvent()
    {
        var msg = await WriteAndRead(
            "Event: Newchannel\r\nChannel: SIP/2000-00000001\r\nUniqueid: 123.1\r\nPrivilege: call,all\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsEvent.Should().BeTrue();
        msg.EventType.Should().Be("Newchannel");
        msg["Channel"].Should().Be("SIP/2000-00000001");
        msg["Uniqueid"].Should().Be("123.1");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseProtocolIdentifier()
    {
        var msg = await WriteAndRead("Asterisk Call Manager/6.0.0\r\n");

        msg.Should().NotBeNull();
        msg!.IsProtocolIdentifier.Should().BeTrue();
        msg.ProtocolIdentifier.Should().Be("Asterisk Call Manager/6.0.0");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldHandleCommandResponseFollows()
    {
        var msg = await WriteAndRead(
            "Response: Follows\r\nActionID: 2\r\nPrivilege: Command\r\n"
            + "Asterisk 18.0.0 on Linux x86_64\r\n"
            + "Built on 2021-01-01\r\n"
            + "--END COMMAND--\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeTrue();
        msg.ResponseStatus.Should().Be("Follows");
        msg.CommandOutput.Should().Contain("Asterisk 18.0.0");
        msg.CommandOutput.Should().Contain("Built on 2021-01-01");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseMultipleMessages()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Response: Success\r\nActionID: 1\r\n\r\n"
            + "Event: FullyBooted\r\nStatus: Fully Booted\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        var msg1 = await reader.ReadMessageAsync();
        msg1.Should().NotBeNull();
        msg1!.IsResponse.Should().BeTrue();

        var msg2 = await reader.ReadMessageAsync();
        msg2.Should().NotBeNull();
        msg2!.IsEvent.Should().BeTrue();
        msg2.EventType.Should().Be("FullyBooted");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldReturnNull_WhenPipeCompleted()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);
        await pipe.Writer.CompleteAsync();

        var msg = await reader.ReadMessageAsync();
        msg.Should().BeNull();
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldBeCaseInsensitive()
    {
        var msg = await WriteAndRead("response: success\r\nactionid: ABC\r\nmessage: OK\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeTrue();
        msg.ActionId.Should().Be("ABC");
    }

    [Fact]
    public async Task ReadMessageAsync_DuplicateKeys_LastValueWins()
    {
        var msg = await WriteAndRead("Event: Test\r\nVar: first\r\nVar: second\r\n\r\n");

        msg.Should().NotBeNull();
        msg!["Var"].Should().Be("second");
    }

    // ── PipeReader buffer regression tests (Bug 1: AdvanceTo examined) ──────────
    // These tests keep the pipe writer open (no CompleteAsync) to simulate a live
    // TCP stream. Without the fix, the second ReadMessageAsync blocks because the
    // PipeReader considers remaining bytes "already examined".

    [Fact]
    public async Task ReadMessageAsync_ShouldParseSecondMessage_WhenTwoMessagesInSingleWrite()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Response: Success\r\nActionID: 1\r\n\r\n"
            + "Event: FullyBooted\r\nStatus: Fully Booted\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        // Writer NOT completed — simulates live TCP stream

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var msg1 = await reader.ReadMessageAsync(cts.Token);
        msg1.Should().NotBeNull();
        msg1!.IsResponse.Should().BeTrue();
        msg1.ActionId.Should().Be("1");

        var msg2 = await reader.ReadMessageAsync(cts.Token);
        msg2.Should().NotBeNull();
        msg2!.IsEvent.Should().BeTrue();
        msg2.EventType.Should().Be("FullyBooted");

        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseThreeMessages_WhenAllInSingleWrite()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Response: Success\r\nActionID: 1\r\n\r\n"
            + "Event: Newchannel\r\nChannel: SIP/2000\r\n\r\n"
            + "Event: Hangup\r\nChannel: SIP/2000\r\nCause: 16\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var msg1 = await reader.ReadMessageAsync(cts.Token);
        msg1.Should().NotBeNull();
        msg1!.IsResponse.Should().BeTrue();

        var msg2 = await reader.ReadMessageAsync(cts.Token);
        msg2.Should().NotBeNull();
        msg2!.IsEvent.Should().BeTrue();
        msg2.EventType.Should().Be("Newchannel");

        var msg3 = await reader.ReadMessageAsync(cts.Token);
        msg3.Should().NotBeNull();
        msg3!.IsEvent.Should().BeTrue();
        msg3.EventType.Should().Be("Hangup");
        msg3["Cause"].Should().Be("16");

        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldParseResponseAfterProtocolIdentifier_WhenBothInSingleWrite()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        var data = "Asterisk Call Manager/6.0.0\r\n"
            + "Response: Success\r\nActionID: 1\r\nMessage: OK\r\n\r\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var msg1 = await reader.ReadMessageAsync(cts.Token);
        msg1.Should().NotBeNull();
        msg1!.IsProtocolIdentifier.Should().BeTrue();
        msg1.ProtocolIdentifier.Should().Be("Asterisk Call Manager/6.0.0");

        var msg2 = await reader.ReadMessageAsync(cts.Token);
        msg2.Should().NotBeNull();
        msg2!.IsResponse.Should().BeTrue();
        msg2["Message"].Should().Be("OK");

        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldWaitForMoreData_WhenMessageIsFragmented()
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        // Write only the first half of a message (no terminating \r\n\r\n)
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("Event: Newchannel\r\nChannel: SIP/2000\r\n"));

        // Read should NOT complete yet — message is incomplete.
        // Use a generous delay for slow CI runners.
        var readTask = reader.ReadMessageAsync(CancellationToken.None).AsTask();
        var delayTask = Task.Delay(TimeSpan.FromSeconds(1));
        var winner = await Task.WhenAny(readTask, delayTask);
        winner.Should().Be(delayTask, "reader should block waiting for more data");

        // Now write the terminating blank line
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var msg = await readTask.WaitAsync(cts.Token);
        msg.Should().NotBeNull();
        msg!.IsEvent.Should().BeTrue();
        msg.EventType.Should().Be("Newchannel");

        await pipe.Writer.CompleteAsync();
    }
}
