using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public class AmiProtocolWriterTests
{
    [Fact]
    public async Task WriteActionAsync_ShouldWriteBasicAction()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("Ping", "1");

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Be("Action: Ping\r\nActionID: 1\r\n\r\n");
    }

    [Fact]
    public async Task WriteActionAsync_ShouldWriteActionWithFields()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("Originate", "42",
        [
            new("Channel", "SIP/2000"),
            new("Context", "default"),
            new("Exten", "1234"),
            new("Priority", "1")
        ]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Action: Originate\r\n");
        output.Should().Contain("ActionID: 42\r\n");
        output.Should().Contain("Channel: SIP/2000\r\n");
        output.Should().Contain("Context: default\r\n");
        output.Should().Contain("Exten: 1234\r\n");
        output.Should().Contain("Priority: 1\r\n");
        output.Should().EndWith("\r\n\r\n");
    }

    [Fact]
    public async Task WriteFieldsAsync_ShouldWriteRawFields()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteFieldsAsync(
        [
            new("Action", "Login"),
            new("Username", "admin"),
            new("Secret", "pass123")
        ]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Action: Login\r\n");
        output.Should().Contain("Username: admin\r\n");
        output.Should().Contain("Secret: pass123\r\n");
        output.Should().EndWith("\r\n\r\n");
    }

    [Fact]
    public async Task WriteActionAsync_ShouldHandleUtf8Characters()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);

        await writer.WriteActionAsync("UserEvent", "99",
            [new("UserEvent", "TestEvent"), new("Data", "valor-especial")]);

        pipe.Writer.Complete();
        var result = await pipe.Reader.ReadAsync();
        var output = Encoding.UTF8.GetString(result.Buffer.FirstSpan);

        output.Should().Contain("Data: valor-especial\r\n");
    }

    [Fact]
    public async Task RoundTrip_WriterThenReader_ShouldProduceParsableMessage()
    {
        var pipe = new Pipe();
        var writer = new AmiProtocolWriter(pipe.Writer);
        var reader = new AmiProtocolReader(pipe.Reader);

        await writer.WriteActionAsync("QueueStatus", "77",
            [new("Queue", "sales")]);
        pipe.Writer.Complete();

        var msg = await reader.ReadMessageAsync();

        msg.Should().NotBeNull();
        msg!["Action"].Should().Be("QueueStatus");
        msg["ActionID"].Should().Be("77");
        msg["Queue"].Should().Be("sales");
    }
}
