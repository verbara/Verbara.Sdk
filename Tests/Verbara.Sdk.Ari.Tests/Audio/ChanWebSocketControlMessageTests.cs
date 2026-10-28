using Verbara.Sdk.Ari.Audio;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class ChanWebSocketControlMessageTests
{
    // The inbound round trips of the SDK's own `kind` JSON were replaced by facts fed captured Asterisk frames
    // (WebSocketAudioSessionTests: every captured control message and MEDIA_START, through the session's read pump).
    // What remains is the outbound serializer, whose bytes change with the outbound task.

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
}
