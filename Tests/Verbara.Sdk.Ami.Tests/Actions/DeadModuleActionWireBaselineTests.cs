using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Generated;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Actions;

/// <summary>
/// The 28 actions of dead Asterisk modules (chan_sip, res_monitor, app_meetme, Skype for Asterisk, the Zaptel names,
/// <c>AgentCallbackLogin</c>, app_setcdruserfield) serialize to the same action name and the same fields as before their
/// deprecation mark: each populated instance goes through <see cref="GeneratedActionSerializer"/> and is compared with
/// the line captured on the tree before the mark.
/// </summary>
/// <remarks>
/// A deprecation mark changes what the compiler reports, never what the SDK writes. These lines were captured on the
/// unmarked tree, so a mark that changed the generated serializer's output for one of them fails here.
/// </remarks>
public sealed class DeadModuleActionWireBaselineTests
{
#pragma warning disable VSDK0005 // pins the deprecated surface until its removal in 2.10.0
    /// <summary>Each marked action, populated with a distinct value in every property it declares.</summary>
    private static readonly Dictionary<string, Func<ManagerAction>> Populated = new(StringComparer.Ordinal)
    {
        [nameof(SipPeersAction)] = static () => new SipPeersAction(),
        [nameof(SipShowPeerAction)] = static () => new SipShowPeerAction { Peer = "100" },
        [nameof(SipShowRegistryAction)] = static () => new SipShowRegistryAction(),
        [nameof(SipNotifyAction)] = static () => new SipNotifyAction { Channel = "SIP/100" },
        [nameof(MonitorAction)] = static () => new MonitorAction { Channel = "SIP/100-00000001", File = "rec-1", Format = "wav", Mix = true },
        [nameof(StopMonitorAction)] = static () => new StopMonitorAction { Channel = "SIP/100-00000001" },
        [nameof(ChangeMonitorAction)] = static () => new ChangeMonitorAction { Channel = "SIP/100-00000001", File = "rec-2" },
        [nameof(PauseMonitorAction)] = static () => new PauseMonitorAction { Channel = "SIP/100-00000001" },
        [nameof(UnpauseMonitorAction)] = static () => new UnpauseMonitorAction { Channel = "SIP/100-00000001" },
        [nameof(MeetMeMuteAction)] = static () => new MeetMeMuteAction(),
        [nameof(MeetMeUnmuteAction)] = static () => new MeetMeUnmuteAction(),
        [nameof(SkypeAccountPropertyAction)] = static () => new SkypeAccountPropertyAction { User = "alice" },
        [nameof(SkypeAddBuddyAction)] = static () => new SkypeAddBuddyAction { User = "alice", Buddy = "bob", AuthMsg = "hi" },
        [nameof(SkypeBuddiesAction)] = static () => new SkypeBuddiesAction { User = "alice" },
        [nameof(SkypeBuddyAction)] = static () => new SkypeBuddyAction { User = "alice", Buddy = "bob" },
        [nameof(SkypeChatSendAction)] = static () => new SkypeChatSendAction { Skypename = "alice.s", User = "alice", Message = "hello" },
        [nameof(SkypeLicenseListAction)] = static () => new SkypeLicenseListAction(),
        [nameof(SkypeLicenseStatusAction)] = static () => new SkypeLicenseStatusAction(),
        [nameof(SkypeRemoveBuddyAction)] = static () => new SkypeRemoveBuddyAction { User = "alice", Buddy = "bob" },
        [nameof(ZapDialOffhookAction)] = static () => new ZapDialOffhookAction { ZapChannel = 3, Number = "5551234" },
        [nameof(ZapDndOffAction)] = static () => new ZapDndOffAction { ZapChannel = 3 },
        [nameof(ZapDndOnAction)] = static () => new ZapDndOnAction { ZapChannel = 3 },
        [nameof(ZapHangupAction)] = static () => new ZapHangupAction { ZapChannel = 3 },
        [nameof(ZapRestartAction)] = static () => new ZapRestartAction(),
        [nameof(ZapShowChannelsAction)] = static () => new ZapShowChannelsAction(),
        [nameof(ZapTransferAction)] = static () => new ZapTransferAction { ZapChannel = 3 },
        [nameof(AgentCallbackLoginAction)] = static () => new AgentCallbackLoginAction
        {
            Agent = "1001", Exten = "2001", Context = "agents", AckCall = true, WrapupTime = 15000,
        },
        [nameof(SetCdrUserFieldAction)] = static () => new SetCdrUserFieldAction { Channel = "SIP/100-00000001", UserField = "campaign-7", Append = false },
    };

    /// <summary>What each populated action serialized to on the unmarked tree: <c>name|Key=Value|…</c>, in write order.</summary>
    private static readonly Dictionary<string, string> Captured = new(StringComparer.Ordinal)
    {
        [nameof(AgentCallbackLoginAction)] = "AgentCallbackLogin|Agent=1001|Exten=2001|Context=agents|AckCall=true|WrapupTime=15000",
        [nameof(ChangeMonitorAction)] = "ChangeMonitor|Channel=SIP/100-00000001|File=rec-2",
        [nameof(MeetMeMuteAction)] = "MeetMeMute",
        [nameof(MeetMeUnmuteAction)] = "MeetMeUnmute",
        [nameof(MonitorAction)] = "Monitor|Channel=SIP/100-00000001|File=rec-1|Format=wav|Mix=true",
        [nameof(PauseMonitorAction)] = "PauseMonitor|Channel=SIP/100-00000001",
        [nameof(SetCdrUserFieldAction)] = "SetCDRUserField|Channel=SIP/100-00000001|UserField=campaign-7|Append=false",
        [nameof(SipNotifyAction)] = "SipNotify|Channel=SIP/100",
        [nameof(SipPeersAction)] = "SIPPeers",
        [nameof(SipShowPeerAction)] = "SIPShowPeer|Peer=100",
        [nameof(SipShowRegistryAction)] = "SipShowRegistry",
        [nameof(SkypeAccountPropertyAction)] = "SkypeAccountProperty|User=alice",
        [nameof(SkypeAddBuddyAction)] = "SkypeAddBuddy|User=alice|Buddy=bob|AuthMsg=hi",
        [nameof(SkypeBuddiesAction)] = "SkypeBuddies|User=alice",
        [nameof(SkypeBuddyAction)] = "SkypeBuddy|User=alice|Buddy=bob",
        [nameof(SkypeChatSendAction)] = "SkypeChatSend|Skypename=alice.s|User=alice|Message=hello",
        [nameof(SkypeLicenseListAction)] = "SkypeLicenseList",
        [nameof(SkypeLicenseStatusAction)] = "SkypeLicenseStatus",
        [nameof(SkypeRemoveBuddyAction)] = "SkypeRemoveBuddy|User=alice|Buddy=bob",
        [nameof(StopMonitorAction)] = "StopMonitor|Channel=SIP/100-00000001",
        [nameof(UnpauseMonitorAction)] = "UnpauseMonitor|Channel=SIP/100-00000001",
        [nameof(ZapDialOffhookAction)] = "ZapDialOffhook|ZapChannel=3|Number=5551234",
        [nameof(ZapDndOffAction)] = "ZapDNDOff|ZapChannel=3",
        [nameof(ZapDndOnAction)] = "ZapDNDOn|ZapChannel=3",
        [nameof(ZapHangupAction)] = "ZapHangup|ZapChannel=3",
        [nameof(ZapRestartAction)] = "ZapRestart",
        [nameof(ZapShowChannelsAction)] = "ZapShowChannels",
        [nameof(ZapTransferAction)] = "ZapTransfer|ZapChannel=3",
    };
#pragma warning restore VSDK0005

    public static TheoryData<string> MarkedActions => [.. Populated.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(MarkedActions))]
    public void Serialize_ShouldWriteTheCapturedNameAndFields_WhenADeadModuleActionIsSerialized(string action)
    {
        Render(Populated[action]()).Should().Be(Captured[action],
            "a deprecation mark changes what the compiler reports, never the name or fields the SDK writes");
    }

    [Fact]
    public void Baseline_ShouldCoverExactlyTheTwentyEightMarkedActions_WhenItIsRead()
    {
        Populated.Keys.Should().HaveCount(28, "the ruled set holds 28 actions of dead modules")
            .And.BeEquivalentTo(Captured.Keys, "every marked action has its captured line, and no other action does");
    }

    private static string Render(ManagerAction action) =>
        string.Join('|', GeneratedActionSerializer.Serialize(action).Select(static f => $"{f.Key}={f.Value}")
            .Prepend(GeneratedActionSerializer.GetActionName(action)));
}
