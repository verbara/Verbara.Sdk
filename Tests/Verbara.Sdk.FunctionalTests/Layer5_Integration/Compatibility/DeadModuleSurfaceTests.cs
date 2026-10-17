namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Compatibility;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;
using AmiConnection = Verbara.Sdk.Ami.Connection.AmiConnection;

/// <summary>
/// The AMI surface of dead Asterisk modules, against the pinned image. The SDK marks obsolete the actions of
/// chan_sip, res_monitor, app_meetme, the Zaptel names, chan_agent's <c>AgentCallbackLogin</c>,
/// app_setcdruserfield and Skype for Asterisk, and the events whose wire name Asterisk 12 renamed or removed. This
/// guard pins the premise of each mark on the image the lane runs: none of the marked names is registered or listed,
/// and every replacement whose module the image loads is. A re-pinned image that brings a marked name back fails
/// here, naming it, so the item is re-ruled before the image is pinned.
/// </summary>
/// <remarks>
/// <para>
/// <c>ListCommands</c> lists only the actions the user's write classes may run; <c>testadmin</c> holds
/// <c>system</c>, <c>call</c>, <c>agent</c>, <c>user</c>, <c>config</c>, <c>originate</c>, <c>command</c>,
/// <c>reporting</c> and <c>dtmf</c>, which cover every name below. The absence half is also read from the CLI's
/// <c>manager show commands</c>, which no write class filters.
/// </para>
/// <para>
/// The <c>DAHDI&lt;name&gt;</c> replacements of the Zaptel actions are not checked: the image does not build
/// chan_dahdi. AMI must be enabled for the command list to be complete: without <c>manager.conf</c> the list lacks
/// the actions registered by <c>manager.c</c> itself.
/// </para>
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class DeadModuleSurfaceTests : FunctionalTestBase
{
    /// <summary>The AMI names the 28 marked action types send (their <c>VerbaraMapping</c>).</summary>
    private static readonly string[] MarkedActions =
    [
        "SIPPeers", "SIPShowPeer", "SipShowRegistry", "SipNotify",
        "Monitor", "StopMonitor", "ChangeMonitor", "PauseMonitor", "UnpauseMonitor",
        "MeetMeMute", "MeetMeUnmute",
        "ZapDialOffhook", "ZapDNDOff", "ZapDNDOn", "ZapHangup", "ZapRestart", "ZapShowChannels", "ZapTransfer",
        "AgentCallbackLogin",
        "SetCDRUserField",
        "SkypeAccountProperty", "SkypeAddBuddy", "SkypeBuddies", "SkypeBuddy", "SkypeChatSend", "SkypeLicenseList",
        "SkypeLicenseStatus", "SkypeRemoveBuddy",
    ];

    /// <summary>The replacement actions the marks name, whose module the image loads.</summary>
    private static readonly string[] ReplacementActions =
    [
        "PJSIPShowEndpoints", "PJSIPShowEndpoint", "PJSIPShowRegistrationsOutbound", "PJSIPNotify",
        "MixMonitor", "StopMixMonitor", "MixMonitorMute",
        "ConfbridgeMute", "ConfbridgeUnmute",
        "SetVar",
    ];

    /// <summary>The wire names of the marked event types.</summary>
    private static readonly string[] MarkedEvents =
    [
        "QueueMemberPaused", "AgentCallbackLogin", "AgentCallbackLogoff", "ChannelReload", "Transfer", "MusicOnHold",
        "FaxReceived", "DTMF", "ChannelUpdate", "JabberEvent",
    ];

    /// <summary>The live event names that replace them.</summary>
    private static readonly string[] ReplacementEvents =
    [
        "QueueMemberPause", "MusicOnHoldStart", "MusicOnHoldStop", "ReceiveFAX", "DTMFBegin", "DTMFEnd",
        "BlindTransfer", "AttendedTransfer", "AgentLogin", "AgentLogoff",
    ];

    /// <summary>The header fields of a <c>ListCommands</c> response that are not action names.</summary>
    private static readonly string[] ResponseHeaders = ["Response", "ActionID", "Message"];

    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    [Fact]
    public async Task ListCommands_ShouldRegisterNoMarkedActionAndEveryReplacement_WhenTheImageHasBooted()
    {
        await using var connection = await ConnectBootedAsync();

        var response = await connection.SendActionAsync(new ListCommandsAction());
        var registered = (response.RawFields?.Keys ?? [])
            .Except(ResponseHeaders, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cliRegistered = FirstColumn(await RunCliAsync(connection, "manager show commands"));

        using (new AssertionScope())
        {
            response.Response.Should().Be("Success", "ListCommands is a core manager action");
            registered.Should().Contain("ListCommands", "a complete list names the action that produced it");
            registered.Should().Contain("Command", "the list holds the core actions, so it is not empty or truncated");
            foreach (var name in MarkedActions)
            {
                registered.Should().NotContain(name,
                    $"'{name}' belongs to a dead module the SDK marks obsolete; an image that registers it re-opens the ruling");
                cliRegistered.Should().NotContain(name,
                    $"'{name}' must not be registered under any write class either");
            }

            foreach (var name in ReplacementActions)
            {
                registered.Should().Contain(name,
                    $"'{name}' is the replacement a mark names, and its module is loaded on the pinned image");
            }
        }
    }

    [Fact]
    public async Task ManagerShowEvents_ShouldListNoMarkedEventAndEveryReplacement_WhenTheImageHasBooted()
    {
        await using var connection = await ConnectBootedAsync();

        var listed = AllWords(await RunCliAsync(connection, "manager show events"));

        using (new AssertionScope())
        {
            listed.Should().Contain("FullyBooted", "a complete event list holds the core events");
            foreach (var name in MarkedEvents)
            {
                listed.Should().NotContain(name,
                    $"'{name}' is a wire name Asterisk renamed or removed; an image that documents it re-opens the ruling");
            }

            foreach (var name in ReplacementEvents)
            {
                listed.Should().Contain(name, $"'{name}' is the live event name a mark names");
            }
        }
    }

    /// <summary>A connection on an Asterisk that has loaded every module, so the lists are complete.</summary>
    private async Task<AmiConnection> ConnectBootedAsync()
    {
        var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(60);
            opts.AutoReconnect = false;
        });
        try
        {
            await connection.ConnectAsync();
            await RunCliAsync(connection, "core waitfullybooted");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> RunCliAsync(AmiConnection connection, string command)
    {
        var response = await connection.SendActionAsync<CommandResponse>(new CommandAction { Command = command });
        response.Output.Should().NotBeNullOrWhiteSpace($"'{command}' answers with output");
        return response.Output!;
    }

    /// <summary>The first word of each line: the action column of <c>manager show commands</c>.</summary>
    private static HashSet<string> FirstColumn(string output) =>
        output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split(Separators, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every word: <c>manager show events</c> prints its names in three columns.</summary>
    private static HashSet<string> AllWords(string output) =>
        output.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
