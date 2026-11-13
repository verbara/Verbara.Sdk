using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[Obsolete(
    "No supported Asterisk emits JitterBufStats; chan_iax2 last emitted it in Asterisk 11. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("JitterBufStats")]
public sealed class JitterBufStatsEvent : ManagerEvent
{
    public string? Owner { get; set; }
    public int? Ping { get; set; }
    public int? LocalJitter { get; set; }
    public int? LocalJbDelay { get; set; }
    public int? LocalTotalLost { get; set; }
    public int? LocalLossPercent { get; set; }
    public int? LocalDropped { get; set; }
    public int? Localooo { get; set; }
    public int? LocalReceived { get; set; }
    public int? RemoteJitter { get; set; }
    public int? RemoteJbDelay { get; set; }
    public int? RemoteTotalLost { get; set; }
    public int? RemoteLossPercent { get; set; }
    public int? RemoteDropped { get; set; }
    public int? Remoteooo { get; set; }
    public int? RemoteReceived { get; set; }
}

