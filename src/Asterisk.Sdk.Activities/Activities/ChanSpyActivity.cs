using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Direction of ChanSpy operation — which legs of the target channel the supervisor listens to.</summary>
public enum ChanSpyMode
{
    /// <summary>Listen to both sides of the conversation (default).</summary>
    Both = 0,
    /// <summary>Listen only to the audio spoken by the target (outbound from target).</summary>
    SpyOnly = 1,
    /// <summary>Whisper audio to the target without being heard by the other party.</summary>
    WhisperOnly = 2,
    /// <summary>Listen to both and whisper back — full supervisor coaching mode.</summary>
    Coach = 3,
}

/// <summary>
/// Launch the <c>ChanSpy</c> dialplan application from the supervisor's AGI channel to monitor
/// another active channel. Depending on <see cref="Mode"/>, the supervisor can listen silently,
/// whisper coaching audio to the target, or do both.
/// </summary>
/// <remarks>
/// <see cref="BargeActivity"/> covers the "join as audible third party" case (dialplan option <c>B</c>).
/// For ARI-side equivalent use <see cref="SnoopActivity"/>.
/// </remarks>
public sealed class ChanSpyActivity(IAgiChannel channel) : ActivityBase(channel)
{
    /// <summary>
    /// Target channel spec. Examples: a full channel name (<c>PJSIP/alice-000001</c>),
    /// a prefix (<c>PJSIP/alice</c>) to match any channel for that endpoint, or <c>null</c>
    /// to spy across all channels (requires <see cref="Options"/> to narrow scope).
    /// </summary>
    public string? TargetChannel { get; init; }

    /// <summary>ChanSpy operation mode.</summary>
    public ChanSpyMode Mode { get; init; } = ChanSpyMode.Both;

    /// <summary>
    /// Additional ChanSpy options appended after the mode flags. Examples: <c>q</c> (quiet),
    /// <c>s</c> (skip unspecified channels), <c>E</c> (exit on <c>#</c>).
    /// See Asterisk documentation for the full option list.
    /// </summary>
    public string? Options { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var modeFlags = Mode switch
        {
            ChanSpyMode.SpyOnly => "o",
            ChanSpyMode.WhisperOnly => "w",
            ChanSpyMode.Coach => "W",
            _ => string.Empty,
        };

        var combined = modeFlags + (Options ?? string.Empty);
        var args = (TargetChannel, combined) switch
        {
            (null, "") => string.Empty,
            (null, _) => $",{combined}",
            (_, "") => TargetChannel!,
            _ => $"{TargetChannel},{combined}",
        };

        await Channel.ExecAsync("ChanSpy", args, cancellationToken);
    }
}
