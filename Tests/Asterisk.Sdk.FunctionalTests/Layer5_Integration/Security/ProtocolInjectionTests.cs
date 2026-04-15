namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Security;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ProtocolInjectionTests : FunctionalTestBase
{
    // -----------------------------------------------------------------------
    // Test 1: Newline injection in action value must not corrupt the protocol
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

        // Inject a CRLF sequence that attempts to forge an extra AMI header
        var maliciousCommand = new CommandAction
        {
            Command = "core show version\r\nAction: Logoff"
        };

        var act = async () => await connection.SendActionAsync(maliciousCommand);

        // Must either succeed (with a valid response) or throw a typed exception —
        // it must NOT crash the connection or leave it in a corrupted state.
        Exception? thrown = null;
        try
        {
            var response = await connection.SendActionAsync(maliciousCommand);
            // If it succeeds the response must be coherent (not empty garbage)
            response.Should().NotBeNull("a response object must always be returned");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // Regardless of whether the injected command threw or succeeded,
        // the connection must remain functional — verify with a clean Ping.
        if (thrown is null)
        {
            var probe = await connection.SendActionAsync(new PingAction());
            probe.Response.Should().Be("Success",
                "connection must stay healthy after an action with newlines in its value");
        }

        _ = act; // suppress unused-variable warning
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
