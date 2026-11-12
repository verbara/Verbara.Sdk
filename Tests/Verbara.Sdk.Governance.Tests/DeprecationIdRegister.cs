namespace Verbara.Sdk.Governance.Tests;

/// <summary>One symbol, by the package that declares it and its declaration key.</summary>
/// <param name="Package">The project folder under <c>src/</c> that declares it, for example <c>Verbara.Sdk.Ami</c>.</param>
/// <param name="Key">The type's name, or <c>Type.Member</c> for a member.</param>
internal sealed record RemovedSymbol(string Package, string Key);

/// <summary>Whether symbols still carry a deprecation id.</summary>
internal enum DeprecationIdState
{
    /// <summary>No symbol carries the id; the release that removed its symbols is recorded, and the id is never reused.</summary>
    Retired,

    /// <summary>A closed set of shipped symbols carries the id until the release that removes them.</summary>
    Marked,
}

/// <summary>One row of <see cref="DeprecationIdRegister"/>.</summary>
/// <param name="Id">The diagnostic id, <c>VSDK</c> and four digits.</param>
/// <param name="State">Retired or marked.</param>
/// <param name="Release">For a retired id, the release that removed its symbols; for a marked id, the release that removes them.</param>
/// <param name="GuidePath">The repository path of the guide whose heading the id's warning links, or <see langword="null"/> when it never had one.</param>
/// <param name="Members">For a marked id, the closed set of shipped symbols that carry it; empty for a retired id.</param>
/// <param name="RemovedSet">For a retired id, the symbols it marked that must not be declared again; empty when none is listed.</param>
internal sealed record DeprecationIdRow(
    string Id,
    DeprecationIdState State,
    string Release,
    string? GuidePath,
    IReadOnlyList<RemovedSymbol> Members,
    IReadOnlyList<RemovedSymbol> RemovedSet)
{
    /// <summary>The fragment of the id's heading in its guide: GitHub's slug of the id, which is the id in lower case.</summary>
    public string Anchor => DeprecationIdScanner.Slug(Id);

    /// <summary>
    /// The link every mark of the id carries as its <c>UrlFormat</c>: a literal with no <c>{0}</c>, because the compiler
    /// writes the id there in upper case and GitHub's anchor is lower case.
    /// </summary>
    public string? Link => GuidePath is null ? null : DeprecationIdRegister.RepositoryBlob + GuidePath + "#" + Anchor;
}

/// <summary>
/// The SDK's own deprecation ids, retired or marked. A mark whose id is not here fails the unit lane
/// (<see cref="DeprecationIdScanner"/>), and so does a marked id's mark without the link, the message or the member
/// this table gives it. The id lives here and as a literal on each mark, nowhere else: a guard that reads syntax can
/// read only a literal.
/// </summary>
/// <remarks>
/// A marked id that leaves becomes a retired row and keeps its guide, so the links in binaries already shipped keep
/// opening a heading.
/// </remarks>
internal static class DeprecationIdRegister
{
    /// <summary>The prefix of every guide link: the guides stay on <c>main</c> after the release that removes the symbols.</summary>
    public const string RepositoryBlob = "https://github.com/verbara/Verbara.Sdk/blob/main/";

    public const string RemovalGuide2_9 = "docs/guides/obsolete-ami-events-removal-migration.md";

    public const string MigrationGuide2_10 = "docs/guides/migration-2.10.0.md";

    private const string Ami = "Verbara.Sdk.Ami";
    private const string Ari = "Verbara.Sdk.Ari";
    private const string Sessions = "Verbara.Sdk.Sessions";

    public static IReadOnlyList<DeprecationIdRow> Rows { get; } =
    [
        new("VSDK0001", DeprecationIdState.Retired, "2.9.0", null, [], []),
        Marked("VSDK0002",
            [
                new(Ami, "StatusEvent.CallerId"), new(Ami, "StatusEvent.Account"), new(Ami, "StatusEvent.State"),
                new(Ami, "StatusEvent.BridgedChannel"), new(Ami, "StatusEvent.Link"), new(Ami, "StatusEvent.BridgedUniqueId"),
            ]),
        Marked("VSDK0003", [new(Ari, "AudioStreamMetrics.BufferUnderruns")]),
        Marked("VSDK0004",
            [
                new(Ari, "IChanWebSocketSession.SendXonAsync"), new(Ari, "IChanWebSocketSession.SendXoffAsync"),
                new(Ari, "ChanWebSocketHangup"), new(Ari, "ChanWebSocketMediaBuffering"), new(Ari, "ChanWebSocketDtmf.DurationMs"),
            ]),
        new("VSDK0005", DeprecationIdState.Retired, "2.10.0", RemovalGuide2_9, [], BuildVsdk0005RemovedSet()),
        Marked("VSDK0006", [new(Sessions, "SessionMetrics.SessionsTimedOut"), new(Sessions, "SessionMetrics.SessionsOrphaned")]),
    ];

    public static DeprecationIdRow Row(string id) => Rows.Single(r => r.Id == id);

    private static DeprecationIdRow Marked(string id, IReadOnlyList<RemovedSymbol> members) =>
        new(id, DeprecationIdState.Marked, "2.11.0", MigrationGuide2_10, members, []);

    /// <summary>The surface of dead Asterisk modules that 2.9.0 marked with <c>VSDK0005</c> and 2.10.0 removed: 49 types and one member.</summary>
    private static List<RemovedSymbol> BuildVsdk0005RemovedSet()
    {
        string[] actions =
        [
            "SipPeersAction", "SipShowPeerAction", "SipShowRegistryAction", "SipNotifyAction",
            "MonitorAction", "StopMonitorAction", "ChangeMonitorAction", "PauseMonitorAction", "UnpauseMonitorAction",
            "MeetMeMuteAction", "MeetMeUnmuteAction",
            "SkypeAccountPropertyAction", "SkypeAddBuddyAction", "SkypeBuddiesAction", "SkypeBuddyAction",
            "SkypeChatSendAction", "SkypeLicenseListAction", "SkypeLicenseStatusAction", "SkypeRemoveBuddyAction",
            "ZapDialOffhookAction", "ZapDndOffAction", "ZapDndOnAction", "ZapHangupAction", "ZapRestartAction",
            "ZapShowChannelsAction", "ZapTransferAction",
            "AgentCallbackLoginAction", "SetCdrUserFieldAction",
        ];
        string[] responses = ["SipShowPeerResponse", "SkypeBuddyResponse", "SkypeLicenseStatusResponse"];
        string[] events =
        [
            "AgentCallbackLoginEvent", "AgentCallbackLogoffEvent", "QueueMemberPausedEvent", "MusicOnHoldEvent",
            "FaxReceivedEvent", "DtmfEvent", "TransferEvent", "ChannelReloadEvent", "ChannelUpdateEvent", "JabberEventEvent",
            "AgiExecEvent", "AsyncAgiEvent", "SendFaxStatusEvent", "BridgeExecEvent", "ModuleLoadReportEvent",
        ];

        return
        [
            .. actions.Concat(responses).Concat(events).Select(n => new RemovedSymbol(Ami, n)),
            new(Ami, "MeetMeEventBase"),
            new(Ami, "MeetmeCompatibility"),
            new("Verbara.Sdk.Agi", "MeetmeCommand"),
            new("Verbara.Sdk.Activities", "MeetmeActivity.UseConfBridge"),
        ];
    }
}
