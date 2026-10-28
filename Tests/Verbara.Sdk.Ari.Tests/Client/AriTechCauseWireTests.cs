using System.Buffers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Ari.Resources;
using Verbara.Sdk.Ari.Tests.TestSupport;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// Asterisk writes <c>tech_cause</c> on ChannelDestroyed and ChannelHangupRequest as a JSON integer (the
/// channel driver's own cause, a SIP response code for PJSIP) and leaves it out when the driver set none.
/// The event model carries it as text, so the parser must read the number into that text instead of
/// failing the whole event. Every frame below goes through <see cref="AriClient.ParseEvent"/>, the parser
/// the client and the outbound listener both use.
/// </summary>
public sealed class AriTechCauseWireTests
{
    // The frame from the bug report (Asterisk 22.11.0, PJSIP, the callee answered 486 Busy Here), with the
    // context and application values the reporter elided filled in.
    private const string ReportedChannelDestroyed = """
        {
          "type": "ChannelDestroyed",
          "timestamp": "2026-10-16T15:53:13.952+0330",
          "cause": 17,
          "cause_txt": "User busy",
          "tech_cause": 486,
          "channel": {
            "id": "1792153388.110",
            "name": "PJSIP/301-0000005a",
            "state": "Ringing",
            "caller": { "name": "301", "number": "301" },
            "connected": { "name": "", "number": "" },
            "dialplan": { "context": "from-internal", "exten": "s", "priority": 1, "app_name": "AppDial2", "app_data": "(Outgoing Line)" },
            "language": "en"
          },
          "application": "app"
        }
        """;

    private const string ReportedChannelHangupRequest = """
        {
          "type": "ChannelHangupRequest",
          "timestamp": "2026-10-16T15:53:13.950+0330",
          "cause": 17,
          "tech_cause": 486,
          "channel": {
            "id": "1792153388.110",
            "name": "PJSIP/301-0000005a",
            "state": "Ringing",
            "language": "en"
          },
          "application": "app"
        }
        """;

    // Captured verbatim from Asterisk 22.11.0 over /ari/events: a PJSIP call answered 486 Busy Here, and
    // one answered 503 Service Unavailable. The same frames, with tech_cause a JSON integer, come from
    // 20.20.1, 22.10.1, 23.4.1 and 23.5.0.
    private const string CapturedChannelDestroyed486 =
        """
        {"type":"ChannelDestroyed","timestamp":"2026-10-28T12:07:00.367+0000","cause":17,"cause_txt":"User busy","tech_cause":486,"channel":{"id":"1793189220.2","name":"PJSIP/loop-00000002","state":"Busy","protocol_id":"11aff2c4-5457-4097-88f7-9146018f2087","caller":{"name":"Anonymous","number":"anonymous"},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"busy","exten":"1002","priority":1,"app_name":"","app_data":""},"creationtime":"2026-10-28T12:07:00.367+0000","language":"en"},"asterisk_id":"9a:ed:50:f9:a4:2e","application":"cap"}
        """;

    private const string CapturedChannelHangupRequest503 =
        """
        {"cause":34,"tech_cause":503,"type":"ChannelHangupRequest","timestamp":"2026-10-28T12:07:00.368+0000","channel":{"id":"1793189220.1","name":"PJSIP/loop-00000001","state":"Down","protocol_id":"05b857ac-ef69-486b-8c6a-074559b2ad3b","caller":{"name":"","number":""},"connected":{"name":"","number":""},"accountcode":"","dialplan":{"context":"busy","exten":"s","priority":1,"app_name":"AppDial2","app_data":"(Outgoing Line)"},"creationtime":"2026-10-28T12:07:00.367+0000","language":"en"},"asterisk_id":"9a:ed:50:f9:a4:2e","application":"cap"}
        """;

    [Fact]
    public void ParseEvent_ShouldDeliverTheCapturedChannelDestroyed_WithItsCauses()
    {
        var evt = AriClient.ParseEvent(CapturedChannelDestroyed486);

        var destroyed = evt.Should().BeOfType<ChannelDestroyedEvent>().Subject;
        destroyed.Cause.Should().Be(17);
        destroyed.CauseTxt.Should().Be("User busy");
        destroyed.TechCause.Should().Be("486");
        destroyed.Channel!.Name.Should().Be("PJSIP/loop-00000002");
        destroyed.Application.Should().Be("cap");
    }

    [Fact]
    public void ParseEvent_ShouldDeliverTheCapturedChannelHangupRequest_WithItsCauses()
    {
        var evt = AriClient.ParseEvent(CapturedChannelHangupRequest503);

        var request = evt.Should().BeOfType<ChannelHangupRequestEvent>().Subject;
        request.Cause.Should().Be(34);
        request.TechCause.Should().Be("503");
        request.Channel!.Name.Should().Be("PJSIP/loop-00000001");
    }

    [Fact]
    public void ParseEvent_ShouldDeliverChannelDestroyed_WhenTechCauseIsAJsonNumber()
    {
        var evt = AriClient.ParseEvent(ReportedChannelDestroyed);

        var destroyed = evt.Should().BeOfType<ChannelDestroyedEvent>().Subject;
        destroyed.Cause.Should().Be(17);
        destroyed.CauseTxt.Should().Be("User busy");
        destroyed.TechCause.Should().Be("486");
        destroyed.Channel!.Id.Should().Be("1792153388.110");
        destroyed.Channel.Name.Should().Be("PJSIP/301-0000005a");
        destroyed.RawJson.Should().Be(ReportedChannelDestroyed);
    }

    [Fact]
    public void ParseEvent_ShouldDeliverChannelHangupRequest_WhenTechCauseIsAJsonNumber()
    {
        var evt = AriClient.ParseEvent(ReportedChannelHangupRequest);

        var request = evt.Should().BeOfType<ChannelHangupRequestEvent>().Subject;
        request.Cause.Should().Be(17);
        request.TechCause.Should().Be("486");
        request.Channel!.Id.Should().Be("1792153388.110");
    }

    [Theory]
    [InlineData("ChannelDestroyed", "0", "0")]
    [InlineData("ChannelDestroyed", "-1", "-1")]
    [InlineData("ChannelDestroyed", "603", "603")]
    [InlineData("ChannelDestroyed", "\"487\"", "487")]
    [InlineData("ChannelHangupRequest", "0", "0")]
    [InlineData("ChannelHangupRequest", "-1", "-1")]
    [InlineData("ChannelHangupRequest", "\"487\"", "487")]
    public void ParseEvent_ShouldCarryTechCauseAsItsText_WhenTheValueIsANumberOrAString(
        string type, string wireValue, string expected)
    {
        var json = $$$"""{"type":"{{{type}}}","cause":16,"tech_cause":{{{wireValue}}},"channel":{"id":"ch-1","name":"PJSIP/a-1","state":"Up"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().NotBeNull();
        TechCauseOf(evt!).Should().Be(expected);
    }

    [Theory]
    [InlineData("ChannelDestroyed", "")]
    [InlineData("ChannelDestroyed", ",\"tech_cause\":null")]
    [InlineData("ChannelHangupRequest", "")]
    [InlineData("ChannelHangupRequest", ",\"tech_cause\":null")]
    public void ParseEvent_ShouldLeaveTechCauseNull_WhenAsteriskSendsNone(string type, string techCauseMember)
    {
        var json = $$$"""{"type":"{{{type}}}","cause":16{{{techCauseMember}}},"channel":{"id":"ch-1","name":"PJSIP/a-1","state":"Up"}}""";

        var evt = AriClient.ParseEvent(json);

        evt.Should().NotBeNull();
        evt!.Type.Should().Be(type);
        TechCauseOf(evt).Should().BeNull();
    }

    [Fact]
    public void ParseEvent_ShouldNameTheEventTypeInTheWarning_WhenAFrameCannotBeRead()
    {
        // A value of a type Asterisk never sends for this field still fails the frame; the warning
        // then has to say which event was lost, not only how long the frame was.
        const string json = """{"type":"ChannelDestroyed","cause":16,"tech_cause":{"code":486},"channel":{"id":"ch-1"}}""";
        var logger = new CapturingLogger<AriClient>();

        var evt = AriClient.ParseEvent(json, logger);

        evt.Should().BeNull();
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Value("EventType").Should().Be("ChannelDestroyed");
        warning.Value("JsonLength").Should().Be(json.Length);
        warning.Message.Should().Contain("event_type=ChannelDestroyed");
    }

    [Theory]
    [InlineData("486", "\"tech_cause\":\"486\"")]
    [InlineData(null, "\"tech_cause\":null")]
    public void Serialize_ShouldWriteTechCauseAsAString_AsItDidBeforeTheConverter(string? techCause, string expected)
    {
        var destroyed = new ChannelDestroyedEvent { Type = "ChannelDestroyed", TechCause = techCause };
        var request = new ChannelHangupRequestEvent { Type = "ChannelHangupRequest", TechCause = techCause };

        JsonSerializer.Serialize(destroyed, AriJsonContext.Default.ChannelDestroyedEvent).Should().Contain(expected);
        JsonSerializer.Serialize(request, AriJsonContext.Default.ChannelHangupRequestEvent).Should().Contain(expected);
    }

    [Fact]
    public void Read_ShouldKeepTheNumberText_WhenTheNumberSpansTwoBufferSegments()
    {
        // A reader over a segmented buffer (a pipe, a pooled stream) hands a number split across segments
        // as a value sequence rather than a span.
        var first = new Segment(Encoding.UTF8.GetBytes("{\"tech_cause\":4"));
        var last = first.Append(Encoding.UTF8.GetBytes("86}"));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        reader.Read();
        reader.Read();
        reader.Read();

        reader.HasValueSequence.Should().BeTrue();
        new AriNumberAsTextConverter().Read(ref reader, typeof(string), AriJsonContext.Default.Options).Should().Be("486");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static string? TechCauseOf(AriEvent evt) => evt switch
    {
        ChannelDestroyedEvent destroyed => destroyed.TechCause,
        ChannelHangupRequestEvent request => request.TechCause,
        _ => throw new InvalidOperationException($"unexpected event {evt.GetType().Name}"),
    };
}
