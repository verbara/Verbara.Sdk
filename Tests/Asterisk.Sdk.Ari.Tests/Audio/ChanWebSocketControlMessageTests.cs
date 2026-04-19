using System.Text.Json;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class ChanWebSocketControlMessageTests
{
    // ------------------------------------------------------------------------
    // Round-trip serialization (Inbound messages)
    // ------------------------------------------------------------------------

    [Fact]
    public void MediaStart_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMediaStart("slin16", 16000, 1);

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMediaStart>().Which.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void MediaXoff_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMediaXoff();

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMediaXoff>();
    }

    [Fact]
    public void MediaXon_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMediaXon();

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMediaXon>();
    }

    [Fact]
    public void MediaBuffering_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMediaBuffering(4096);

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMediaBuffering>()
            .Which.Bytes.Should().Be(4096);
    }

    [Fact]
    public void MediaMarkProcessed_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMediaMarkProcessed("prompt-end");

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMediaMarkProcessed>()
            .Which.Mark.Should().Be("prompt-end");
    }

    [Fact]
    public void Dtmf_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketDtmf("5", 120);

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        var dtmf = decoded.Should().BeOfType<ChanWebSocketDtmf>().Subject;
        dtmf.Digit.Should().Be("5");
        dtmf.DurationMs.Should().Be(120);
    }

    [Fact]
    public void Hangup_ShouldRoundTrip_WithCause_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketHangup("Normal Clearing");

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketHangup>()
            .Which.Cause.Should().Be("Normal Clearing");
    }

    [Fact]
    public void Hangup_ShouldRoundTrip_WithNullCause_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketHangup(Cause: null);

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketHangup>()
            .Which.Cause.Should().BeNull();
    }

    // ------------------------------------------------------------------------
    // Round-trip serialization (Outbound messages)
    // ------------------------------------------------------------------------

    [Fact]
    public void MarkMedia_ShouldRoundTrip_WhenSerializedAndDeserialized()
    {
        var original = new ChanWebSocketMarkMedia("tts-sentence-42");

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);
        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        decoded.Should().BeOfType<ChanWebSocketMarkMedia>()
            .Which.Mark.Should().Be("tts-sentence-42");
    }

    [Theory]
    [InlineData(ChanWebSocketMediaDirection.In, "in")]
    [InlineData(ChanWebSocketMediaDirection.Out, "out")]
    [InlineData(ChanWebSocketMediaDirection.Both, "both")]
    public void SetMediaDirection_ShouldRoundTrip_WhenAllDirectionValues(
        ChanWebSocketMediaDirection direction,
        string expectedWireValue)
    {
        var original = new ChanWebSocketSetMediaDirection(direction);

        var json = ChanWebSocketControlMessageSerializer.Serialize(original);

        json.Should().Contain($"\"direction\":\"{expectedWireValue}\"");

        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);
        decoded.Should().BeOfType<ChanWebSocketSetMediaDirection>()
            .Which.Direction.Should().Be(direction);
    }

    // ------------------------------------------------------------------------
    // Discriminator & wire-format validation
    // ------------------------------------------------------------------------

    [Fact]
    public void Serialize_ShouldEmitKindDiscriminator_WhenAnyMessageSerialized()
    {
        var json = ChanWebSocketControlMessageSerializer.Serialize(
            new ChanWebSocketMediaStart("ulaw", 8000, 1));

        json.Should().StartWith("{\"kind\":\"media_start\"");
    }

    [Fact]
    public void Serialize_ShouldEmitSnakeCaseProperties_WhenDtmfSerialized()
    {
        var json = ChanWebSocketControlMessageSerializer.Serialize(
            new ChanWebSocketDtmf("#", 80));

        json.Should().Contain("\"duration_ms\":80");
        json.Should().Contain("\"digit\":\"#\"");
    }

    [Fact]
    public void Serialize_ShouldEmitSnakeCaseDiscriminator_WhenMediaMarkProcessedSerialized()
    {
        var json = ChanWebSocketControlMessageSerializer.Serialize(
            new ChanWebSocketMediaMarkProcessed("m1"));

        json.Should().Contain("\"kind\":\"media_mark_processed\"");
    }

    [Fact]
    public void Deserialize_ShouldThrowJsonException_WhenDiscriminatorUnknown()
    {
        const string json = """{"kind":"unknown_message_type","foo":"bar"}""";

        Action act = () => ChanWebSocketControlMessageSerializer.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Deserialize_ShouldThrow_WhenDiscriminatorMissing()
    {
        // STJ's polymorphic reader throws NotSupportedException for payloads missing
        // the type discriminator on an abstract base (documented behaviour).
        const string json = """{"format":"slin16","rate":16000,"channels":1}""";

        Action act = () => ChanWebSocketControlMessageSerializer.Deserialize(json);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*type discriminator*");
    }

    [Fact]
    public void Deserialize_ShouldReadUtf8Bytes_WhenSpanOverloadUsed()
    {
        var utf8 = ChanWebSocketControlMessageSerializer.SerializeToUtf8Bytes(
            new ChanWebSocketMediaBuffering(1024));

        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(utf8.AsSpan());

        decoded.Should().BeOfType<ChanWebSocketMediaBuffering>()
            .Which.Bytes.Should().Be(1024);
    }

    [Fact]
    public void Deserialize_ShouldAcceptRealAsteriskPayload_WhenMediaStartReceived()
    {
        // Simulates an actual Asterisk 23.2+ media_start text frame.
        const string asteriskJson =
            """{"kind":"media_start","format":"slin16","rate":16000,"channels":1}""";

        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(asteriskJson);

        var start = decoded.Should().BeOfType<ChanWebSocketMediaStart>().Subject;
        start.Format.Should().Be("slin16");
        start.Rate.Should().Be(16000);
        start.Channels.Should().Be(1);
    }

    [Fact]
    public void Deserialize_ShouldBeCaseInsensitive_WhenPropertyCasingDiffers()
    {
        // JsonSourceGenerationOptions.PropertyNameCaseInsensitive = true.
        const string json =
            """{"kind":"dtmf","Digit":"*","Duration_Ms":150}""";

        var decoded = ChanWebSocketControlMessageSerializer.Deserialize(json);

        var dtmf = decoded.Should().BeOfType<ChanWebSocketDtmf>().Subject;
        dtmf.Digit.Should().Be("*");
        dtmf.DurationMs.Should().Be(150);
    }
}
