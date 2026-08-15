using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Cartesia;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Cartesia;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>CartesiaFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from recorded
/// frames (D4), not from a different server.
/// </summary>
public class CartesiaSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly CartesiaFakeServer _server;

    public CartesiaSpeechSynthesizerTests()
    {
        _server = new CartesiaFakeServer();
        _server.Start();
    }

    private CartesiaSpeechSynthesizer BuildSynthesizer()
        => new(Options.Create(new CartesiaOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice"
        }), fakeServerPort: _server.Port);

    /// <summary>The audio the fake replays, read from the same tree the fake reads.</summary>
    private static byte[] RecordedAudio => CartesiaFakeServer.ReadFrameBytes(CartesiaFakeServer.AudioChunk);

    [Fact]
    public async Task SynthesizeAsync_ShouldSendRequestJson_WithModelAndVoice()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var request = _server.ReceivedJsonMessages[0];
        request.Should().Contain("\"model_id\":\"sonic-3\"");
        request.Should().Contain("\"id\":\"test-voice\"");
        request.Should().Contain("\"transcript\":\"hola mundo\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        // The point of the recording: a real waveform of a length that is NOT chunk-aligned
        // traverses the frame path, so a partial final frame reaches the consumer. Two 320-byte
        // arrays of zeros — exact multiples — could never produce one.
        //
        // Note what is NOT asserted: that the final frame is exactly `length % 320`. Frame count
        // and boundaries are the transport's business, not the client's contract; what the client
        // owes is every byte, in order, with nothing invented and nothing dropped.
        var expected = RecordedAudio;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        (expected.Length % CartesiaFakeServer.AudioFrameSize).Should()
            .NotBe(0, "the recording must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the recording must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= CartesiaFakeServer.AudioFrameSize);
        frames.Should().Contain(f => f.Length != CartesiaFakeServer.AudioFrameSize,
            "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the recorded audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsDone()
    {
        // The fake sends the recorded audio as binary frames, then the recorded `done` frame.
        // The synthesizer must stop iterating as soon as "done" arrives — with every audio byte
        // delivered and nothing after it.
        _server.SendDoneTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public void RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The fake is only as good as what is on disk: trim a documented
        // field, swap the audio or re-save the JSON and the suite would keep passing while quietly
        // testing something smaller. This fails here, next to the sidecar that explains the file,
        // instead of surfacing three tests away as a puzzling byte mismatch.
        var audio = RecordedAudio;
        audio.Should().HaveCount(2008, "the sidecar records this exact length");
        (audio.Length % CartesiaFakeServer.AudioFrameSize).Should().NotBe(0);

        using var done = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.DoneFrame));
        var root = done.RootElement;
        root.GetProperty("type").GetString().Should().Be("done");

        // The other three documented fields. CartesiaTtsControlMessage models `type` and nothing
        // else, so these reach the parser as unmodelled siblings — which is the whole point of
        // recording the full frame instead of {"type":"done"}.
        root.GetProperty("done").GetBoolean().Should().BeTrue();
        root.GetProperty("status_code").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("context_id").GetString().Should().Be("00000000-0000-0000-0000-000000000000",
            "a correlating identifier is placeholdered, never real (protocol guide §4)");
    }

    [Fact]
    public void RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic. If this
        // fails, either the file was edited or SyntheticPcm changed — both need a sidecar update.
        var regenerated = SyntheticPcm.Triangle(
            CartesiaFakeServer.AudioSampleCount,
            CartesiaFakeServer.AudioPeriodSamples,
            CartesiaFakeServer.AudioAmplitude);

        regenerated.Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerAborts()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
