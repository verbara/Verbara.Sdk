using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

/// <summary>
/// Plays R1 MF (Multi-Frequency) tones on a channel. Asterisk 20+.
/// MF tones are used in legacy analog/T1 signaling (different from DTMF).
/// </summary>
[AsteriskMapping("PlayMF")]
public sealed class PlayMfAction : ManagerAction
{
    /// <summary>The channel on which to play MF tones.</summary>
    public string? Channel { get; set; }
    /// <summary>The MF digit string to play (0-9, A-C, *, #).</summary>
    public string? Digit { get; set; }
}
