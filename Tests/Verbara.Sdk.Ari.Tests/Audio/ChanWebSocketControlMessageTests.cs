using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class ChanWebSocketControlMessageTests
{
    // The inbound round trips of the SDK's own `kind` JSON were replaced by facts fed captured Asterisk frames
    // (WebSocketAudioSessionTests: every captured control message and MEDIA_START, through the session's read pump).
    // The outbound `kind` round trips are rewritten onto the captured command bytes (ChanWebSocketCommandSpelling), and the
    // `kind` discriminator test is gone: nothing writes or reads that shape any more.

    // ------------------------------------------------------------------------
    // The writer: the captured spelling, in the named format
    // ------------------------------------------------------------------------

    [Fact]
    public void SerializeToUtf8Bytes_ShouldWriteTheCapturedMarkMediaSpelling_InEachFormat()
    {
        // Arrange
        var record = new ChanWebSocketMarkMedia("m");

        // Act
        var plain = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.PlainText);
        var json = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.Json);

        // Assert
        using (new AssertionScope())
        {
            plain.Should().Equal(ChanWebSocketCommandSpelling.MarkPlain("m"), "the captured plain-text spelling");
            plain.Should().HaveCount(12);
            json.Should().Equal(ChanWebSocketCommandSpelling.MarkJson("m"), "the captured JSON spelling");
            json.Should().HaveCount(45);
            ChanWebSocketControlMessageSerializer.Serialize(record).Should().Be(
                ChanWebSocketControlMessageSerializer.Serialize(record, ChanWebSocketControlMessageFormat.Json),
                "the format-less writer writes the JSON form");
            ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record).Should().Equal(json);
            foreach (var format in new[] { ChanWebSocketControlMessageFormat.PlainText, ChanWebSocketControlMessageFormat.Json })
            {
                Encoding.UTF8.GetBytes(ChanWebSocketControlMessageSerializer.Serialize(record, format)).Should().Equal(
                    ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, format), "the string overload is the UTF-8 one decoded ({0})", format);
            }
        }
    }

    [Theory]
    [InlineData(ChanWebSocketMediaDirection.In, "in", 50)]
    [InlineData(ChanWebSocketMediaDirection.Out, "out", 51)]
    [InlineData(ChanWebSocketMediaDirection.Both, "both", 52)]
    public void SerializeToUtf8Bytes_ShouldWriteTheCapturedSetMediaDirectionSpelling_InJsonOnly(
        ChanWebSocketMediaDirection direction, string wireValue, int length)
    {
        // Arrange
        var record = new ChanWebSocketSetMediaDirection(direction);

        // Act
        var json = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.Json);
        Action plain = () => ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.PlainText);
        Action plainString = () => ChanWebSocketControlMessageSerializer.Serialize(record, ChanWebSocketControlMessageFormat.PlainText);

        // Assert
        using (new AssertionScope())
        {
            json.Should().Equal(ChanWebSocketCommandSpelling.SetMediaDirectionJson(wireValue), "the captured JSON spelling");
            json.Should().HaveCount(length);
            ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record).Should().Equal(json, "the format-less writer writes the JSON form");
            plain.Should().Throw<NotSupportedException>("Asterisk accepts SET_MEDIA_DIRECTION in JSON only");
            plainString.Should().Throw<NotSupportedException>();
        }
    }

    public static TheoryData<string> EveryRecordAsteriskSends() =>
    [
        nameof(ChanWebSocketMediaStart), nameof(ChanWebSocketMediaXoff), nameof(ChanWebSocketMediaXon),
        nameof(ChanWebSocketMediaMarkProcessed), nameof(ChanWebSocketDtmf), nameof(ChanWebSocketError),
#pragma warning disable VSDK0004 // the obsolete type is the point: a record no capture shows Asterisk sending has no command form either
        nameof(ChanWebSocketUnknownMessage), nameof(ChanWebSocketHangup), nameof(ChanWebSocketMediaBuffering),
#pragma warning restore VSDK0004
    ];

    [Theory]
    [MemberData(nameof(EveryRecordAsteriskSends))]
    public void Serialize_ShouldThrowNotSupportedException_WhenTheRecordIsOneAsteriskSends(string recordType)
    {
        // Arrange
        var record = RecordAsteriskSends(recordType);

        // Act
        Action plain = () => ChanWebSocketControlMessageSerializer.Serialize(record, ChanWebSocketControlMessageFormat.PlainText);
        Action json = () => ChanWebSocketControlMessageSerializer.Serialize(record, ChanWebSocketControlMessageFormat.Json);
        Action plainBytes = () => ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.PlainText);
        Action jsonBytes = () => ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(record, ChanWebSocketControlMessageFormat.Json);

        // Assert — a record Asterisk sends has no command form in either format
        using (new AssertionScope(recordType))
        {
            plain.Should().Throw<NotSupportedException>();
            json.Should().Throw<NotSupportedException>();
            plainBytes.Should().Throw<NotSupportedException>();
            jsonBytes.Should().Throw<NotSupportedException>();
        }
    }

    private static ChanWebSocketControlMessage RecordAsteriskSends(string recordType) => recordType switch
    {
        nameof(ChanWebSocketMediaStart) => new ChanWebSocketMediaStart("slin16", 16000, 1),
        nameof(ChanWebSocketMediaXoff) => new ChanWebSocketMediaXoff(),
        nameof(ChanWebSocketMediaXon) => new ChanWebSocketMediaXon(),
        nameof(ChanWebSocketMediaMarkProcessed) => new ChanWebSocketMediaMarkProcessed("m"),
        nameof(ChanWebSocketDtmf) => new ChanWebSocketDtmf("1", 0),
        nameof(ChanWebSocketError) => new ChanWebSocketError("c", "e"),
        nameof(ChanWebSocketUnknownMessage) => new ChanWebSocketUnknownMessage("FUTURE_EVENT", "FUTURE_EVENT"),
#pragma warning disable VSDK0004 // the obsolete type is the point: it has no command form either
        nameof(ChanWebSocketHangup) => new ChanWebSocketHangup(null),
        nameof(ChanWebSocketMediaBuffering) => new ChanWebSocketMediaBuffering(0),
#pragma warning restore VSDK0004
        _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
    };

    [Fact]
    public void Serialize_ShouldThrowForTheArgument_WhenTheMessageIsNullOrTheFormatIsOutsideTheEnum()
    {
        // Act
        Action nullMessage = () => ChanWebSocketControlMessageSerializer.Serialize(null!, ChanWebSocketControlMessageFormat.Json);
        Action nullMessageBytes = () => ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(null!, ChanWebSocketControlMessageFormat.Json);
        Action badFormat = () => ChanWebSocketControlMessageSerializer.Serialize(new ChanWebSocketMarkMedia("m"), (ChanWebSocketControlMessageFormat)2);
        Action badFormatBytes = () => ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(new ChanWebSocketMarkMedia("m"), (ChanWebSocketControlMessageFormat)2);

        // Assert
        using (new AssertionScope())
        {
            nullMessage.Should().Throw<ArgumentNullException>();
            nullMessageBytes.Should().Throw<ArgumentNullException>();
            badFormat.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("format");
            badFormatBytes.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("format");
        }
    }

    [Fact]
    public void ControlMessageFormat_ShouldBePlainText_WhenTheRecordIsBuiltWithoutIt()
    {
        new ChanWebSocketMediaStart("slin16", 16000, 1).ControlMessageFormat.Should().Be(ChanWebSocketControlMessageFormat.PlainText);
    }

    // ------------------------------------------------------------------------
    // The reader: a text frame as Asterisk sends it
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public void Deserialize_ShouldReadTheCapturedDtmfEnd_ThroughBothOverloads(ChanWebSocketCaptureFormat format)
    {
        // Arrange
        var captured = ChanWebSocketWireCapture.ControlMessages.First(
            m => m.AsteriskVersion == "24.0.0-rc2" && m.Event == "DTMF_END" && m.Format == format);

        // Act
        var fromString = ChanWebSocketControlMessageSerializer.Deserialize(captured.Text);
        var fromBytes = ChanWebSocketControlMessageSerializer.Deserialize(captured.Payload().AsSpan());

        // Assert
        using (new AssertionScope(captured.Source))
        {
            foreach (var dtmf in new[] { fromString, fromBytes }.Select(m => m.Should().BeOfType<ChanWebSocketDtmf>().Subject))
            {
                dtmf.Digit.Should().Be(captured.Value);
                dtmf.ChannelId.Should().Be(captured.ChannelId);
            }
        }
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText, ChanWebSocketControlMessageFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json, ChanWebSocketControlMessageFormat.Json)]
    public void Deserialize_ShouldReadTheCapturedMediaStartWithItsFormat_ThroughBothOverloads(
        ChanWebSocketCaptureFormat captureFormat, ChanWebSocketControlMessageFormat expected)
    {
        // Arrange
        var captured = ChanWebSocketWireCapture.MediaStart.First(f =>
            f.AsteriskVersion == "24.0.0-rc2" && f.Producer == ChanWebSocketCaptureProducer.ExternalMedia && f.Format == captureFormat && f.Option == "none");

        // Act
        var fromString = ChanWebSocketControlMessageSerializer.Deserialize(captured.Text);
        var fromBytes = ChanWebSocketControlMessageSerializer.Deserialize(captured.Payload().AsSpan());

        // Assert
        using (new AssertionScope(captured.Source))
        {
            foreach (var message in new[] { fromString, fromBytes })
            {
                var mediaStart = message.Should().BeOfType<ChanWebSocketMediaStart>().Subject;
                mediaStart.ChannelId.Should().Be(captured.ChannelId);
                mediaStart.ControlMessageFormat.Should().Be(expected, "the format is derived from the frame's grammar");
            }
        }
    }

    [Fact]
    public void Deserialize_ShouldReadTheKindJsonAsTheGenericRecord_WhenGivenTheShapeNothingSends()
    {
        // Arrange — 2.8.x's own `kind` JSON, written in the test: the shape nothing sends any more
        const string kindJson = """{"kind":"dtmf","digit":"5","duration_ms":120}""";

        // Act
        var fromString = ChanWebSocketControlMessageSerializer.Deserialize(kindJson);
        var fromBytes = ChanWebSocketControlMessageSerializer.Deserialize(Encoding.UTF8.GetBytes(kindJson).AsSpan());

        // Assert
        using (new AssertionScope())
        {
            foreach (var message in new[] { fromString, fromBytes })
            {
                var generic = message.Should().BeOfType<ChanWebSocketUnknownMessage>().Subject;
                generic.Event.Should().BeEmpty("the frame names no event");
                generic.RawText.Should().Be(kindJson);
            }
        }
    }

    [Fact]
    public void Deserialize_ShouldThrowArgumentNullException_WhenTheTextIsNull()
    {
        Action act = () => ChanWebSocketControlMessageSerializer.Deserialize((string)null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
