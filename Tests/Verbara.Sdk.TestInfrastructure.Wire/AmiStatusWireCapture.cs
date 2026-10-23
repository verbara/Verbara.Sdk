namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// The replies four supported Asterisk versions sent to one AMI <c>Action: Status</c>, byte for byte, for every
/// test that parses a <c>Status</c> frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> Asterisk 18.26.4, 20.20.1, 22.9.0 and 23.4.1, each asked once over raw TCP AMI
/// (no proxy, no library framing) while the same six legs were up. The dialplan file of that probe was not kept,
/// so it is described here as the frames record it: one context, <c>d5probe</c>, where extension <c>100</c>
/// answers and waits (<c>Wait(120)</c>) and extension <c>200</c> rings without answering.
/// </para>
/// <para>
/// <b>The six legs, on every version.</b> Four are <c>Up</c> on <c>Local/100</c>: the <c>;2</c> leg in
/// <c>Wait</c> (priority 2); the <c>;1</c> leg running <c>Dial(Local/100@d5probe/n,120)</c> (priority 1); and the
/// dialled pair, whose <c>;1</c> leg runs <c>AppDial</c> with an empty <c>Exten</c> and whose <c>;2</c> leg is in
/// <c>Wait</c>. Two are on <c>Local/200</c>: one <c>Ringing</c> (<c>ChannelState: 5</c>, <c>AppDial2</c>) and one
/// <c>Ring</c> (<c>ChannelState: 4</c>, <c>Wait</c>). The header list is the same on all 24 frames.
/// </para>
/// <para>
/// <b>What is kept.</b> Only the reply to the <c>Status</c> action: the <c>Response: Success</c> that opens the
/// list, the six <c>Event: Status</c> frames and the closing <c>StatusComplete</c>. The rest of each transcript
/// is not kept, because it carries the <c>Login</c> exchange. The bytes are CRLF-framed as Asterisk sent them,
/// and the repository marks the files <c>binary</c> so no checkout converts them.
/// </para>
/// <para>
/// <b>Adding to it.</b> Only from a capture: a hand-written frame is a guess about the wire, which is the one thing
/// this fixture exists to exclude. A new version is captured with <c>scripts/capture-ami-recording.py</c>; none of
/// its workloads reproduces this six-leg shape today, so adding a version means adding that workload first.
/// </para>
/// </remarks>
public static class AmiStatusWireCapture
{
    /// <summary>The Asterisk versions captured, oldest first.</summary>
    public static IReadOnlyList<string> Versions { get; } = ["18.26.4", "20.20.1", "22.9.0", "23.4.1"];

    /// <summary>The number of <c>Event: Status</c> frames in every version's reply.</summary>
    public const int StatusFramesPerVersion = 6;

    /// <summary>
    /// The captured reply of <paramref name="version"/> (one of <see cref="Versions"/>), exactly as it came off the
    /// wire. A fresh array each call, so a parser that writes through its input cannot corrupt the next test's copy.
    /// </summary>
    public static byte[] StatusReply(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var major = version.Split('.')[0];
        if (!Versions.Contains(version))
            throw new ArgumentOutOfRangeException(nameof(version), version,
                "Only captured versions are served: " + string.Join(", ", Versions) + ".");

        var name = "AmiStatus/ast" + major + ".status.raw";
        using var stream = typeof(AmiStatusWireCapture).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "The capture " + name + " is not embedded in " + typeof(AmiStatusWireCapture).Assembly.GetName().Name + ".");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
