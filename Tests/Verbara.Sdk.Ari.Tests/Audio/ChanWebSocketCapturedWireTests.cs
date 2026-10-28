using System.Text;
using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The captured chan_websocket <c>MEDIA_START</c> frames (<see cref="ChanWebSocketWireCapture"/>) read as data,
/// before any parser of this repository is held to them. Each entry must be the frame its provenance says it is:
/// a <c>MEDIA_START</c> in its declared format, naming the call it declares, at the length the wire carried. The
/// fixture is shared so every suite that reads this frame reads the same bytes.
/// </summary>
/// <remarks>
/// The JSON entries are read with <see cref="JsonDocument"/>, never with the SDK's chan_websocket model: the
/// capture exists to correct that model, so it cannot be the judge of the capture.
/// </remarks>
public sealed class ChanWebSocketCapturedWireTests
{
    public static TheoryData<int> EveryCapturedFrame()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ChanWebSocketWireCapture.MediaStart.Count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCapturedFrame))]
    public void MediaStart_ShouldNameMediaStartAndItsCall_WhenReadingEachCapturedFrame(int index)
    {
        var frame = ChanWebSocketWireCapture.MediaStart[index];

        frame.Text.Should().NotBeNullOrWhiteSpace(frame.Source);
        frame.Source.Should().NotBeNullOrWhiteSpace();
        frame.AsteriskVersion.Should().BeOneOf(ChanWebSocketWireCapture.Versions);

        var (eventName, channelId) = ReadEventAndChannelId(frame);

        eventName.Should().Be("MEDIA_START", "{0} is captured as the connection's first text frame", frame.Source);
        channelId.Should().Be(frame.ChannelId, "the entry declares the call its frame names ({0})", frame.Source);
    }

    [Theory]
    [MemberData(nameof(EveryCapturedFrame))]
    public void Payload_ShouldHaveTheCapturedLength_WhenTheCaptureRecordedIt(int index)
    {
        var frame = ChanWebSocketWireCapture.MediaStart[index];

        var payload = frame.Payload();

        payload.Should().NotBeEmpty();
        Encoding.UTF8.GetString(payload).Should().Be(frame.Text);
        if (frame.CapturedLength is { } length)
            payload.Length.Should().Be(length, "the listener recorded {0} bytes for {1}", length, frame.Source);
    }

    [Fact]
    public void MediaStart_ShouldCoverBothFormatsAndBothProducers_OnEveryCapturedVersion()
    {
        foreach (var version in ChanWebSocketWireCapture.Versions)
        {
            var frames = ChanWebSocketWireCapture.MediaStart.Where(f => f.AsteriskVersion == version).ToList();

            frames.Select(f => f.Format).Distinct().Should().BeEquivalentTo(
                [ChanWebSocketCaptureFormat.PlainText, ChanWebSocketCaptureFormat.Json], version);
            frames.Select(f => f.Producer).Distinct().Should().BeEquivalentTo(
                [ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureProducer.ExternalMedia], version);
        }

        ChanWebSocketWireCapture.MediaStart.Select(f => f.AsteriskVersion).Distinct()
            .Should().BeEquivalentTo(ChanWebSocketWireCapture.Versions, "every listed version has captured frames");
    }

    [Theory]
    [MemberData(nameof(EveryCapturedFrame))]
    public void MediaStart_ShouldCarryPassthroughAndUnbuffered_OnlyOnTheVersionsThatSendThem(int index)
    {
        var frame = ChanWebSocketWireCapture.MediaStart[index];

        var fields = ReadPassthroughAndUnbuffered(frame);

        if (!ChanWebSocketWireCapture.VersionsWithPassthroughFields.Contains(frame.AsteriskVersion))
        {
            fields.Should().BeNull("{0} predates the two fields ({1})", frame.AsteriskVersion, frame.Source);
            return;
        }

        var expected = frame.Option switch
        {
            "p" => (true, true),
            "u" => (false, true),
            _ => (false, false),
        };
        fields.Should().Be(expected, "option {0} on {1} ({2})", frame.Option, frame.AsteriskVersion, frame.Source);
    }

    // ---------------------------------------------------------------- the control messages that follow MEDIA_START

    private static readonly string[] CapturedControlEvents =
        ["DTMF_END", "MEDIA_XOFF", "MEDIA_XON", "MEDIA_MARK_PROCESSED", "ERROR", "STATUS", "QUEUE_DRAINED", "MEDIA_BUFFERING_COMPLETED"];

    private static readonly string[] ControlMessageVersions = ["22.10.1", "23.4.1", "22.12.0-rc2", "23.6.0-rc2", "24.0.0-rc2"];

    public static TheoryData<int> EveryCapturedControlMessage()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ChanWebSocketWireCapture.ControlMessages.Count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCapturedControlMessage))]
    public void ControlMessages_ShouldNameTheirEventAndCarryTheirDeclaredValues_WhenReadingEachCapturedFrame(int index)
    {
        var message = ChanWebSocketWireCapture.ControlMessages[index];

        message.Source.Should().NotBeNullOrWhiteSpace();
        message.AsteriskVersion.Should().BeOneOf(ControlMessageVersions);
        message.ConnectionChannelId.Should().NotBeNullOrWhiteSpace();
        Encoding.UTF8.GetString(message.Payload()).Should().Be(message.Text);

        if (message.Format == ChanWebSocketCaptureFormat.Json)
        {
            using var json = JsonDocument.Parse(message.Text);
            var root = json.RootElement;
            root.GetProperty("event").GetString().Should().Be(message.Event, message.Source);
            root.GetProperty("channel_id").GetString().Should().Be(message.ChannelId, message.Source);
            message.ConnectionChannelId.Should().Be(message.ChannelId, "a JSON frame names the call it belongs to ({0})", message.Source);
            var value = message.Event switch
            {
                "DTMF_END" => root.GetProperty("digit").GetString(),
                "MEDIA_MARK_PROCESSED" => root.GetProperty("correlation_id").GetString(),
                "ERROR" => root.GetProperty("error_text").GetString(),
                _ => null,
            };
            value.Should().Be(message.Value, message.Source);
        }
        else
        {
            message.Text.Split(' ')[0].Should().Be(message.Event, message.Source);
            if (message.ChannelId is null)
                message.Text.Should().NotContain("channel_id:", "the entry declares the frame bare ({0})", message.Source);
            else
                message.Text.Should().Contain($" channel_id:{message.ChannelId}", message.Source);
            if (message.Event == "DTMF_END")
                message.Text.Should().Contain($" digit:{message.Value} ", message.Source);
            else if (message.Value is not null)
                message.Text.Should().EndWith(message.Value, "the value is the frame's last field ({0})", message.Source);
        }
    }

    [Fact]
    public void ControlMessages_ShouldCoverEveryCapturedEventInBothFormats_OnEveryVersion()
    {
        foreach (var version in ControlMessageVersions)
        {
            foreach (var format in new[] { ChanWebSocketCaptureFormat.PlainText, ChanWebSocketCaptureFormat.Json })
            {
                var events = ChanWebSocketWireCapture.ControlMessages
                    .Where(m => m.AsteriskVersion == version && m.Format == format)
                    .Select(m => m.Event)
                    .Distinct();
                events.Should().BeEquivalentTo(CapturedControlEvents, "{0} {1}", version, format);
            }
        }

        ChanWebSocketWireCapture.ControlMessages.Should().Contain(
            m => m.Event == "MEDIA_MARK_PROCESSED" && m.Value == "" && m.Format == ChanWebSocketCaptureFormat.PlainText && m.Text == "MEDIA_MARK_PROCESSED",
            "the bare plain-text acknowledgement is captured");
        ChanWebSocketWireCapture.ControlMessages.Should().Contain(
            m => m.Event == "ERROR" && m.Value!.EndsWith('\n'), "an ERROR whose text ends in a newline is captured");
    }

    private static (string? EventName, string? ChannelId) ReadEventAndChannelId(ChanWebSocketCapturedFrame frame)
    {
        if (frame.Format == ChanWebSocketCaptureFormat.Json)
        {
            using var json = JsonDocument.Parse(frame.Text);
            return (json.RootElement.GetProperty("event").GetString(),
                    json.RootElement.GetProperty("channel_id").GetString());
        }

        var tokens = frame.Text.Split(' ');
        var channelId = tokens.Skip(1)
            .Where(t => t.StartsWith("channel_id:", StringComparison.Ordinal))
            .Select(t => t["channel_id:".Length..])
            .SingleOrDefault();
        return (tokens[0], channelId);
    }

    private static (bool Passthrough, bool Unbuffered)? ReadPassthroughAndUnbuffered(ChanWebSocketCapturedFrame frame)
    {
        bool? passthrough = null;
        bool? unbuffered = null;
        if (frame.Format == ChanWebSocketCaptureFormat.Json)
        {
            using var json = JsonDocument.Parse(frame.Text);
            if (json.RootElement.TryGetProperty("passthrough", out var p)) passthrough = p.GetBoolean();
            if (json.RootElement.TryGetProperty("unbuffered", out var u)) unbuffered = u.GetBoolean();
        }
        else
        {
            foreach (var token in frame.Text.Split(' ').Skip(1))
            {
                if (token.StartsWith("passthrough:", StringComparison.Ordinal))
                    passthrough = bool.Parse(token["passthrough:".Length..]);
                else if (token.StartsWith("unbuffered:", StringComparison.Ordinal))
                    unbuffered = bool.Parse(token["unbuffered:".Length..]);
            }
        }

        if (passthrough is null && unbuffered is null)
            return null;
        passthrough.Should().NotBeNull("{0} carries unbuffered, so it carries passthrough too", frame.Source);
        unbuffered.Should().NotBeNull("{0} carries passthrough, so it carries unbuffered too", frame.Source);
        return (passthrough!.Value, unbuffered!.Value);
    }
}
