using System.IO.Pipelines;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// The captured <c>Status</c> replies, parsed the way <c>AmiConnection</c>'s reader loop parses them: the bytes
/// through <see cref="AmiProtocolReader"/>, then each event through <see cref="GeneratedEventDeserializer"/>.
/// </summary>
internal static class StatusWireReplies
{
    /// <summary>One row per captured Asterisk version.</summary>
    public static TheoryData<string> Versions()
    {
        var data = new TheoryData<string>();
        foreach (var version in AmiStatusWireCapture.Versions)
            data.Add(version);
        return data;
    }

    /// <summary>The <see cref="StatusEvent"/>s of <paramref name="version"/>'s reply, in wire order.</summary>
    public static async Task<IReadOnlyList<StatusEvent>> ParseAsync(string version)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(AmiStatusWireCapture.StatusReply(version));
        await pipe.Writer.CompleteAsync();

        var reader = new AmiProtocolReader(pipe.Reader);
        var statuses = new List<StatusEvent>();
        while (await reader.ReadMessageAsync() is { } msg)
        {
            if (msg.IsEvent && GeneratedEventDeserializer.Deserialize(msg) is StatusEvent status)
                statuses.Add(status);
        }

        return statuses;
    }

    /// <summary>Every frame of every version, for a test that asserts over all 24.</summary>
    public static async Task<IReadOnlyList<(string Version, StatusEvent Frame)>> ParseAllAsync()
    {
        var all = new List<(string, StatusEvent)>();
        foreach (var version in AmiStatusWireCapture.Versions)
            all.AddRange((await ParseAsync(version)).Select(frame => (version, frame)));
        return all;
    }

    /// <summary>A header of <paramref name="frame"/> as the wire carried it, or null when the frame has none.</summary>
    public static string? Header(StatusEvent frame, string name) => frame.RawFields?.GetValueOrDefault(name);
}
