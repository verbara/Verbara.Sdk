using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Barge into an active call via <c>ChanSpy</c> with the <c>B</c> (barge) flag — the supervisor
/// becomes an audible third party to both the agent and the customer, bridging all three audio
/// streams. Typical supervisor-intervention pattern.
/// </summary>
/// <remarks>
/// For silent listen-only supervision use <see cref="ChanSpyActivity"/> with
/// <see cref="ChanSpyMode.SpyOnly"/>. For ARI-side equivalent (snoop channel with whisper)
/// use <see cref="SnoopActivity"/>.
/// </remarks>
public sealed class BargeActivity(IAgiChannel channel) : ActivityBase(channel)
{
    /// <summary>
    /// Target channel spec. Examples: full channel name (<c>PJSIP/alice-000001</c>) or a
    /// prefix (<c>PJSIP/alice</c>) to match any channel for that endpoint.
    /// </summary>
    public required string TargetChannel { get; init; }

    /// <summary>
    /// Additional ChanSpy options appended after the barge flag. Examples: <c>q</c> (quiet),
    /// <c>E</c> (exit on <c>#</c>), <c>S</c> (short greeting).
    /// </summary>
    public string? Options { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var combined = "B" + (Options ?? string.Empty);
        await Channel.ExecAsync("ChanSpy", $"{TargetChannel},{combined}", cancellationToken);
    }
}
