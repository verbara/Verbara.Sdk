using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public class AriJsonSerializationTests
{
    [Fact]
    public void AriPlayback_ShouldRoundtripSerialize()
    {
        var original = new AriPlayback { Id = "pb-1", MediaUri = "sound:hello", State = "playing", TargetUri = "channel:ch-1", Language = "en" };

        var json = JsonSerializer.Serialize(original, AriJsonContext.Default.AriPlayback);
        var deserialized = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriPlayback);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be("pb-1");
        deserialized.MediaUri.Should().Be("sound:hello");
        deserialized.State.Should().Be("playing");
        deserialized.Language.Should().Be("en");
    }

    [Fact]
    public void AriLiveRecording_ShouldDeserializeFromJson()
    {
        var json = """{"name":"rec-1","format":"wav","state":"recording","duration":42,"talking_duration":30,"silence_duration":12}""";

        var recording = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLiveRecording);

        recording.Should().NotBeNull();
        recording!.Name.Should().Be("rec-1");
        recording.Format.Should().Be("wav");
        recording.State.Should().Be("recording");
        recording.Duration.Should().Be(42);
        recording.TalkingDuration.Should().Be(30);
        recording.SilenceDuration.Should().Be(12);
    }

    [Fact]
    public void AriStoredRecording_ShouldDeserializeFromJson()
    {
        var json = """{"name":"stored-1","format":"wav"}""";

        var recording = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriStoredRecording);

        recording.Should().NotBeNull();
        recording!.Name.Should().Be("stored-1");
        recording.Format.Should().Be("wav");
    }

    [Fact]
    public void AriEndpoint_ShouldDeserializeFromJson()
    {
        var json = """{"technology":"PJSIP","resource":"2000","state":"online","channel_ids":["ch-1","ch-2"]}""";

        var endpoint = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriEndpoint);

        endpoint.Should().NotBeNull();
        endpoint!.Technology.Should().Be("PJSIP");
        endpoint.Resource.Should().Be("2000");
        endpoint.State.Should().Be("online");
        endpoint.ChannelIds.Should().HaveCount(2);
    }

    [Fact]
    public void AriApplication_ShouldDeserializeFromJson()
    {
        var json = """{"name":"myapp","channel_ids":["ch-1"],"bridge_ids":["br-1"],"endpoint_ids":[],"device_names":[]}""";

        var app = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriApplication);

        app.Should().NotBeNull();
        app!.Name.Should().Be("myapp");
        app.ChannelIds.Should().HaveCount(1);
        app.BridgeIds.Should().HaveCount(1);
    }

    [Fact]
    public void AriSound_ShouldDeserializeWithFormats()
    {
        var json = """{"id":"hello-world","text":"Hello World","formats":[{"language":"en","format":"gsm"},{"language":"es","format":"wav"}]}""";

        var sound = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriSound);

        sound.Should().NotBeNull();
        sound!.Id.Should().Be("hello-world");
        sound.Text.Should().Be("Hello World");
        sound.Formats.Should().HaveCount(2);
        sound.Formats[0].Language.Should().Be("en");
    }

    [Fact]
    public void AriFormatLang_ShouldDeserializeFromJson()
    {
        var json = """{"language":"en","format":"gsm"}""";

        var formatLang = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriFormatLang);

        formatLang.Should().NotBeNull();
        formatLang!.Language.Should().Be("en");
        formatLang.Format.Should().Be("gsm");
    }
}
