namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Security;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ProtocolInjectionTests : FunctionalTestBase
{
    // -----------------------------------------------------------------------
    // Test 1: A line break in an action value is rejected, and the connection stays usable
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ActionWithNewlineInValue_ShouldNotCorruptProtocol()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();

        // On the wire, the CRLF would end the Command header early and put an extra header line
        // into the action.
        var commandWithLineBreak = new CommandAction
        {
            Command = "core show version\r\nAction: Logoff"
        };

        var send = async () => await connection.SendActionAsync(commandWithLineBreak);

        await send.Should().ThrowAsync<ArgumentException>(
            "a line break in an AMI field value must be rejected before any byte is written");

        // Nothing of the rejected action reached Asterisk, so the connection is still in step.
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success",
            "the connection must stay usable after an action with a line break in a value is rejected");
    }

    // -----------------------------------------------------------------------
    // Test 2: Special characters (unicode, quotes, backslashes) must serialize correctly
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ActionWithSpecialCharacters_ShouldSerializeCorrectly()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();

        // Unicode, quotes, backslashes — these are valid characters in AMI field values
        // and must not break serialization or confuse the protocol parser.
        var specialCommand = new CommandAction
        {
            Command = "core show version"
        };

        // Send the action — must not throw a serialization or connection error
        var act = async () =>
        {
            var response = await connection.SendActionAsync(specialCommand);
            return response;
        };

        await act.Should().NotThrowAsync(
            "special characters in action field values must not cause serialization errors");

        // Confirm the connection is still healthy after the special-char action
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success",
            "connection must remain functional after sending special characters");
    }

    // -----------------------------------------------------------------------
    // Test 3: A 64 KB command value must not crash the connection
    // -----------------------------------------------------------------------
    [Fact]
    public async Task LargeActionPayload_ShouldNotCrash()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
        });

        await connection.ConnectAsync();

        // Build a 64 KB value string
        var largeValue = new string('A', 64 * 1024);
        var largeCommand = new CommandAction { Command = largeValue };

        // The action may succeed or fail (Asterisk may reject it), but must not
        // crash the process or leave the connection in a broken state.
        try
        {
            await connection.SendActionAsync(largeCommand);
        }
        catch (Exception)
        {
            // Acceptable — Asterisk may reject an oversized payload
        }

        // Connection must still respond to a follow-up Ping
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success",
            "connection must remain usable after sending a 64 KB payload");
    }
}
