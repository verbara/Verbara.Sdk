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
    public async Task SynthesizeAsync_ShouldSendANonEmptyContextId_WhenTheEndpointRequiresOne()
    {
        // Not cosmetic. The shipped request omitted context_id entirely and the live endpoint
        // answered {"type":"error","status_code":400,"done":true,"error":"context_id is invalid: …"}
        // with zero audio — so this field is the difference between a synthesis and a silence.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        using var request = JsonDocument.Parse(_server.ReceivedJsonMessages[0]);
        request.RootElement.TryGetProperty("context_id", out var contextId).Should().BeTrue();
        contextId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendADistinctContextId_PerRequest()
    {
        // The field exists to correlate the frames of one synthesis; a constant would defeat the
        // only thing it does, and no test on a single request could tell the two apart.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("uno", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await synth.SynthesizeAsync("dos", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var ids = _server.ReceivedJsonMessages
            .Select(m => JsonDocument.Parse(m).RootElement.GetProperty("context_id").GetString())
            .ToList();

        ids.Should().HaveCount(2);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotHalfCloseTheSocket_AfterTheRequest()
    {
        // Measured, not stylistic: the client used to CloseOutputAsync right after the request, and
        // the live endpoint read that frame as "abandon the synthesis" — 0 frames, 0 bytes, against
        // 7 chunks and 32 694 B for a control differing only in that step. The fake records the
        // close instead of reacting to it, so this asserts on what the client did.
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ClientSentCloseFrame.Should().BeFalse(
            "a Close frame before the server answers costs the caller every byte of audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudioInsideTheChunkFrames_WhenTheServerAnswersLikeTheVendor()
    {
        // The measured transport: base64 in `data` on a `chunk` text frame. A live run of the
        // corrected request received seven of these and ZERO binary bytes, so this — not the binary
        // path below — is the case that decides whether callers hear anything.
        //
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
    public async Task SynthesizeAsync_ShouldNotLeakJsonBytesIntoTheAudio_WhenDecodingAChunkFrame()
    {
        // The failure this pins: a receive loop that hands the raw text frame straight through as
        // audio. Every assertion above would still pass on byte count alone if the client yielded
        // the JSON envelope, so this one checks the audio is PCM and not UTF-8 JSON.
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var all = frames.SelectMany(f => f.ToArray()).ToArray();
        all.Should().NotContain((byte)'{');
        all.Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAssembleTheWholeMessage_WhenAChunkFrameArrivesFragmented()
    {
        // The vendor sizes these frames, not this client. A loop that parsed each read as a whole
        // message would hand JSON a truncated document once a frame outgrew the receive buffer —
        // length-dependent, and therefore invisible to every short fixture in this suite.
        _server.TextFrameFragmentBytes = 16;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudio_WhenTheChunkFrameAlsoCarriesUnmodelledFields()
    {
        // The recorded frame, replayed verbatim: `flush_id`, `step_time` and the echoed `context_id`
        // are on it, and the client models none of them. Tolerating an unmapped sibling is the
        // contract; throwing on one would break against a vendor that only ever adds fields.
        _server.SendRecordedChunkFrame = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should()
            .Equal(RecordedAudio.Take(CartesiaFakeServer.AudioFrameSize),
                "the fixture carries the first 320 bytes of the sibling tone");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames_WhenTheServerSendsThem()
    {
        // Tolerated without evidence, deliberately: the live run measured zero binary bytes, but a
        // vendor not sending a mode on one day is not evidence the mode does not exist. This test
        // exists so the branch is not dead code — it is NOT evidence Cartesia sends binary.
        _server.Transport = CartesiaAudioTransport.Binary;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
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

        // The other three documented fields. The client reads none of them, so they reach the
        // parser as unread or unmodelled siblings — which is the whole point of recording the full
        // frame instead of {"type":"done"}.
        root.GetProperty("done").GetBoolean().Should().BeTrue();
        root.GetProperty("status_code").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("context_id").GetString().Should().Be("00000000-0000-0000-0000-000000000000",
            "a correlating identifier is placeholdered, never real (protocol guide §4)");

        // The chunk frame: the seven keys the live probe measured, no more and no fewer. A key set
        // is what that run established — the values on it are our own fiction, so only the names
        // are asserted here.
        using var chunk = JsonDocument.Parse(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.ChunkFrame));
        chunk.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["context_id", "data", "done", "flush_id", "status_code", "step_time", "type"]);
        chunk.RootElement.GetProperty("type").GetString().Should().Be("chunk");
        chunk.RootElement.GetProperty("context_id").GetString().Should()
            .Be("00000000-0000-0000-0000-000000000000");

        // And its audio is the sibling tone, not an independent blob that could drift from it.
        Convert.FromBase64String(chunk.RootElement.GetProperty("data").GetString()!).Should()
            .Equal(audio.Take(CartesiaFakeServer.AudioFrameSize));
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
