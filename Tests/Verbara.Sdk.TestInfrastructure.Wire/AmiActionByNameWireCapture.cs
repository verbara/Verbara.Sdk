using System.Text;

namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// Nine AMI actions written by name over a raw socket to four supported Asterisk versions, and every byte each version
/// sent back, for every test that checks what the SDK writes for an action sent by name or how it reads the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> Captured 2026-11-12 over raw TCP AMI (no library framing; the client bytes were written by a
/// stand-alone probe, never by the SDK's encoder), one login per take with <c>Events: off</c>, on the pinned images
/// of <c>docker/asterisk-base-images.txt</c>, each started by digest with only a <c>manager.conf</c> mounted:
/// <list type="bullet">
/// <item><c>andrius/asterisk:20.20.1_debian-trixie-d0182a6@sha256:c202cdf0f0348ab75e6dee49967418ce8c08853644b8a5c00de91f6b4aca4dd1</c> (Asterisk 20.20.1)</item>
/// <item><c>ghcr.io/verbara/asterisk-base:22.12.0_trixie-c62758c@sha256:470d10b0382e9c822c7b334780f0795e1d09f61e111d798a43b959ba7e959df7</c> (Asterisk 22.12.0)</item>
/// <item><c>ghcr.io/verbara/asterisk-base:23.6.0_trixie-c62758c@sha256:bed956493e84e22841013d7ba356ce68be529bf11fcf65ef95b59cfd0b4eb1b5</c> (Asterisk 23.6.0)</item>
/// <item><c>ghcr.io/verbara/asterisk-base:24.0.0_trixie-c62758c@sha256:09d05c27812adc0ab863be12f325acd71bbba040a3d71c639f07dbdc00a7cd7f</c> (Asterisk 24.0.0)</item>
/// </list>
/// The first of three identical takes per image is kept. Each image answered the same frames in all three.
/// </para>
/// <para>
/// <b>The frames, in the order sent</b> (<see cref="Frames"/>): <c>i</c> <c>Ping</c>; <c>ii</c> <c>LoggerRotate</c>;
/// <c>iii</c> <c>ListCategories</c> with <c>Filename: manager.conf</c>; <c>iv</c> <c>IAXpeers</c> (answered
/// <c>Success</c>, then <c>PeerlistComplete</c> with <c>ListItems: 0</c>); <c>v</c> <c>DeviceStateList</c> (three
/// <c>DeviceStateChange</c>, then <c>DeviceStateListComplete</c> with <c>ListItems: 3</c>); <c>vi</c>
/// <c>devicestatelist</c> (answered as <c>v</c>: the name is matched in any letter case); <c>vii</c>
/// <c>DAHDIDNDon</c> with <c>DAHDIChannel: 1</c> and <c>viii</c> <c>VerbaraNoSuchAction</c> (both
/// <c>Response: Error</c>, <c>Invalid/unknown command</c>: no image builds chan_dahdi); <c>ix</c>
/// <c>FAXSessions</c> (<c>FAXSessionsComplete</c>, <c>ListItems: 0</c>). Every message in the replies carries its
/// frame's <c>ActionID</c>, <c>c27-&lt;image&gt;-1-&lt;frame&gt;</c>.
/// </para>
/// <para>
/// <b>What is kept.</b> Per version, the client bytes of the nine frames concatenated in the order sent, and the
/// server bytes that followed each frame until its action ended, concatenated in the same order. The login, its
/// answer and the logoff are not kept. CRLF is the AMI framing, and the repository marks the files <c>binary</c>.
/// SHA-256 of the files: ast20.client <c>e4bf82e3…98b3</c>, ast20.server <c>1b4e44ea…c5a3</c>, ast22.client
/// <c>cba27dfb…dc7c</c>, ast22.server <c>97ef3e8a…071c</c>, ast23.client <c>1fbcae7b…154f</c>, ast23.server
/// <c>89961c3f…3254</c>, ast24.client <c>8055d66a…a092</c>, ast24.server <c>f855e7e2…589</c>.
/// </para>
/// <para>
/// <b>Adding to it.</b> Only from a capture: a hand-written frame is a guess about the wire.
/// </para>
/// </remarks>
public static class AmiActionByNameWireCapture
{
    /// <summary>The Asterisk versions captured, oldest first.</summary>
    public static IReadOnlyList<string> Versions { get; } = ["20.20.1", "22.12.0", "23.6.0", "24.0.0"];

    /// <summary>The frames, in the order they were sent.</summary>
    public static IReadOnlyList<string> Frames { get; } = ["i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix"];

    /// <summary>
    /// The client bytes of <paramref name="frame"/> as the probe wrote them to <paramref name="version"/>, its
    /// <c>ActionID</c> included.
    /// </summary>
    public static byte[] ClientFrame(string version, string frame) => Pick(Load(version, "client"), frame);

    /// <summary>
    /// Every server message that answered <paramref name="frame"/> on <paramref name="version"/>, in the order
    /// received, with each <c>ActionID</c> header rewritten to <paramref name="actionId"/> so a fake server can replay
    /// it to a client that chose its own.
    /// </summary>
    public static byte[] Reply(string version, string frame, string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        var text = Encoding.Latin1.GetString(Pick(Load(version, "server"), frame));
        var captured = CapturedActionId(version, frame);
        return Encoding.Latin1.GetBytes(text.Replace(
            "ActionID: " + captured + "\r\n", "ActionID: " + actionId + "\r\n", StringComparison.Ordinal));
    }

    /// <summary>The <c>ActionID</c> the probe sent with <paramref name="frame"/> to <paramref name="version"/>.</summary>
    public static string CapturedActionId(string version, string frame) =>
        "c27-" + (version.Split('.')[0] == "20" ? "a20" : "g" + version.Split('.')[0]) + "-1-" + frame;

    private static byte[] Pick(byte[] stream, string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Frames.Contains(frame))
            throw new ArgumentOutOfRangeException(nameof(frame), frame, "Captured frames: " + string.Join(", ", Frames) + ".");

        var suffix = "-1-" + frame + "\r\n";
        var text = Encoding.Latin1.GetString(stream);
        var picked = new StringBuilder();
        foreach (var message in text.Split("\r\n\r\n"))
        {
            if (message.Length == 0)
                continue;
            var line = message.Split("\r\n").FirstOrDefault(l => l.StartsWith("ActionID: c27-", StringComparison.Ordinal));
            if (line is not null && (line + "\r\n").EndsWith(suffix, StringComparison.Ordinal))
                picked.Append(message).Append("\r\n\r\n");
        }

        return Encoding.Latin1.GetBytes(picked.ToString());
    }

    private static byte[] Load(string version, string side)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!Versions.Contains(version))
            throw new ArgumentOutOfRangeException(nameof(version), version,
                "Only captured versions are served: " + string.Join(", ", Versions) + ".");

        var name = "AmiActionByName/ast" + version.Split('.')[0] + "." + side + ".raw";
        using var stream = typeof(AmiActionByNameWireCapture).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "The capture " + name + " is not embedded in " + typeof(AmiActionByNameWireCapture).Assembly.GetName().Name + ".");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
