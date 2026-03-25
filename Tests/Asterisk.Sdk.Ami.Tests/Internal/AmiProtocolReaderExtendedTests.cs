using System.IO.Pipelines;
using System.Text;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public sealed class AmiProtocolReaderExtendedTests
{
    private static async Task<AmiMessage?> WriteAndRead(string data)
    {
        var pipe = new Pipe();
        var reader = new AmiProtocolReader(pipe.Reader);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await pipe.Writer.CompleteAsync();

        return await reader.ReadMessageAsync();
    }

    // ── OpenPBX protocol identifier ────────────────────────────────────

    [Fact]
    public async Task ReadMessageAsync_ShouldParseOpenPbxProtocolIdentifier()
    {
        var msg = await WriteAndRead("OpenPBX Call Manager/1.0\r\n");

        msg.Should().NotBeNull();
        msg!.IsProtocolIdentifier.Should().BeTrue();
        msg.ProtocolIdentifier.Should().Be("OpenPBX Call Manager/1.0");
    }

    // ── Empty command output ───────────────────────────────────────────

    [Fact]
    public async Task ReadMessageAsync_ShouldHandleEmptyCommandOutput()
    {
        // Response: Follows with immediate --END COMMAND-- (no output lines)
        var msg = await WriteAndRead(
            "Response: Follows\r\nActionID: 5\r\nPrivilege: Command\r\n"
            + "--END COMMAND--\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeTrue();
        msg.ResponseStatus.Should().Be("Follows");
        msg.ActionId.Should().Be("5");
        // Command output should be empty (or empty StringBuilder)
        msg.CommandOutput.Should().NotBeNull();
        msg.CommandOutput!.Trim().Should().BeEmpty();
    }

    // ── Output: key accumulation (Asterisk 22+ format) ─────────────────

    [Fact]
    public async Task ReadMessageAsync_ShouldAccumulateOutputKeys()
    {
        // Asterisk 22+ sends "Output: <line>" instead of raw body after Response: Follows
        var msg = await WriteAndRead(
            "Response: Follows\r\nActionID: 3\r\n"
            + "Output: Channel              Location\r\n"
            + "Output: PJSIP/2000           default@100\r\n"
            + "Output: 1 active channel\r\n"
            + "\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeTrue();
        msg.CommandOutput.Should().NotBeNull();
        msg.CommandOutput.Should().Contain("Channel");
        msg.CommandOutput.Should().Contain("PJSIP/2000");
        msg.CommandOutput.Should().Contain("1 active channel");
    }

    [Fact]
    public async Task ReadMessageAsync_ShouldHandleMixedOutputAndHeaders()
    {
        // Output: lines mixed with regular header after Response: Follows
        var msg = await WriteAndRead(
            "Response: Follows\r\nActionID: 7\r\nPrivilege: Command\r\n"
            + "Output: Line 1\r\n"
            + "Output: Line 2\r\n"
            + "\r\n");

        msg.Should().NotBeNull();
        msg!.ActionId.Should().Be("7");
        msg["Privilege"].Should().Be("Command");
        msg.CommandOutput.Should().Contain("Line 1");
        msg.CommandOutput.Should().Contain("Line 2");
    }

    // ── AmiMessage properties ──────────────────────────────────────────

    [Fact]
    public async Task ContainsKey_ShouldReturnTrueForExistingKey()
    {
        var msg = await WriteAndRead("Event: Newchannel\r\nChannel: SIP/100\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.ContainsKey("Event").Should().BeTrue();
        msg.ContainsKey("Channel").Should().BeTrue();
        msg.ContainsKey("NonExistent").Should().BeFalse();
    }

    [Fact]
    public async Task Keys_ShouldReturnAllFieldKeys()
    {
        var msg = await WriteAndRead("Response: Success\r\nActionID: 1\r\nMessage: OK\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.Keys.Should().Contain("Response");
        msg.Keys.Should().Contain("ActionID");
        msg.Keys.Should().Contain("Message");
    }

    [Fact]
    public async Task Fields_ShouldReturnReadOnlyDictionary()
    {
        var msg = await WriteAndRead("Event: Test\r\nFoo: bar\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.Fields.Should().NotBeNull();
        msg.Fields.Should().ContainKey("Event");
        msg.Fields["Event"].Should().Be("Test");
        msg.Fields["Foo"].Should().Be("bar");
    }

    [Fact]
    public async Task Indexer_ShouldReturnNull_WhenKeyNotFound()
    {
        var msg = await WriteAndRead("Event: Test\r\n\r\n");

        msg.Should().NotBeNull();
        msg!["NonExistentKey"].Should().BeNull();
    }

    // ── Case insensitivity ─────────────────────────────────────────────

    [Fact]
    public async Task ContainsKey_ShouldBeCaseInsensitive()
    {
        var msg = await WriteAndRead("Event: Newchannel\r\nChannel: SIP/100\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.ContainsKey("event").Should().BeTrue();
        msg.ContainsKey("EVENT").Should().BeTrue();
        msg.ContainsKey("channel").Should().BeTrue();
    }

    // ── Protocol identifier variations ─────────────────────────────────

    [Fact]
    public async Task ReadMessageAsync_ShouldReturnProtocolIdentifier_AsCaseInsensitive()
    {
        var msg = await WriteAndRead("ASTERISK CALL MANAGER/7.0.0\r\n");

        msg.Should().NotBeNull();
        msg!.IsProtocolIdentifier.Should().BeTrue();
    }

    // ── Command output with multi-line content ─────────────────────────

    [Fact]
    public async Task ReadMessageAsync_ShouldPreserveCommandOutputLines()
    {
        var msg = await WriteAndRead(
            "Response: Follows\r\nActionID: 10\r\n"
            + "Line one of output\r\n"
            + "Line two of output\r\n"
            + "Line three of output\r\n"
            + "--END COMMAND--\r\n\r\n");

        msg.Should().NotBeNull();
        var output = msg!.CommandOutput;
        output.Should().NotBeNull();

        var lines = output!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        lines.Should().HaveCount(3);
        lines[0].Should().Be("Line one of output");
        lines[1].Should().Be("Line two of output");
        lines[2].Should().Be("Line three of output");
    }

    // ── IsEvent / IsResponse / IsProtocolIdentifier ────────────────────

    [Fact]
    public async Task IsEvent_ShouldBeFalse_ForResponse()
    {
        var msg = await WriteAndRead("Response: Success\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsEvent.Should().BeFalse();
        msg.IsResponse.Should().BeTrue();
        msg.IsProtocolIdentifier.Should().BeFalse();
    }

    [Fact]
    public async Task IsResponse_ShouldBeFalse_ForEvent()
    {
        var msg = await WriteAndRead("Event: FullyBooted\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.IsResponse.Should().BeFalse();
        msg.IsEvent.Should().BeTrue();
    }

    [Fact]
    public async Task EventType_ShouldReturnNull_ForResponse()
    {
        var msg = await WriteAndRead("Response: Success\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.EventType.Should().BeNull();
    }

    [Fact]
    public async Task ResponseStatus_ShouldReturnNull_ForEvent()
    {
        var msg = await WriteAndRead("Event: FullyBooted\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.ResponseStatus.Should().BeNull();
    }

    // ── ActionId for events ────────────────────────────────────────────

    [Fact]
    public async Task ActionId_ShouldReturnNull_WhenNotPresent()
    {
        var msg = await WriteAndRead("Event: FullyBooted\r\nStatus: Fully Booted\r\n\r\n");

        msg.Should().NotBeNull();
        msg!.ActionId.Should().BeNull();
    }
}
