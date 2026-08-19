using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>SpeechmaticsFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from the
/// frames in <c>Recordings/speechmatics-stt/</c> (D4), not from a different server. Speechmatics STT
/// is <c>permitted</c> for capturing Output (<c>docs/guides/provider-recording-protocol.md</c> §7 —
/// ToS §10.3 assigns the customer all IP in Transcripts), so — unlike Deepgram and AssemblyAI — its
/// terms are not what stands between this suite and a real capture, and as of 2026-08-18 neither is a
/// missing credential: a live session has streamed audio through this surface and elicited real
/// transcript frames (§4.7). Those frames were read to settle how the segment is assembled and were
/// deliberately not stored, so the fixtures keep §7's documentation-derived route —
/// <c>class: "synthetic"</c> with a <c>source_schema</c> block — and a capture run remains the missing
/// upgrade. What the live session <em>did</em> settle is that the vendor sends the field set these
/// frames claim, which closes the drift half of the D4 gap for this schema while leaving the values
/// half open. The Speechmatics <em>TTS</em> suite is a separate, HTTP-transport surface and does
/// migrate (§4.5).
/// </summary>
public class SpeechmaticsSpeechRecognizerTests : IAsyncDisposable
{
    private readonly SpeechmaticsFakeServer _server;

    public SpeechmaticsSpeechRecognizerTests()
    {
        _server = new SpeechmaticsFakeServer();
        _server.Start();
    }

    /// <summary>
    /// Build a recognizer pointed at this test's fake server through <c>BaseUri</c> — the same
    /// option an operator sets to pick a region — rather than through a test-only constructor.
    /// </summary>
    /// <remarks>
    /// There used to be an <c>internal</c> constructor taking a fake port, and
    /// <c>SpeechmaticsSpeechRecognizer.BuildUri</c> branched on it. The consequence was that the
    /// production URI expression was executed by no test at all: every assertion about the session
    /// URL — including "the credential is not in it" — was made against a line that only ever ran
    /// under test. Configuring <c>BaseUri</c> costs nothing (its own validation admits <c>ws://</c>)
    /// and makes these tests exercise the expression that ships.
    /// </remarks>
    private SpeechmaticsSpeechRecognizer BuildRecognizer(Action<SpeechmaticsOptions>? configure = null)
    {
        var opts = new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v2",
        };
        configure?.Invoke(opts);
        return new SpeechmaticsSpeechRecognizer(Options.Create(opts));
    }

    /// <summary>
    /// The transcript the SDK is expected to yield for a recorded frame: the vendor's own assembled
    /// segment at <c>metadata.transcript</c>, trimmed of the inter-segment glue whitespace the
    /// service pads finals with. Derived from the recording rather than hard-coded, so the frame's
    /// bytes and the client must agree.
    /// </summary>
    private static string RecordedVendorTranscript(string frame)
    {
        using var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(frame));
        return document.RootElement.GetProperty("metadata").GetProperty("transcript").GetString()!.Trim();
    }

    /// <summary>
    /// What the shipped client produced before this fix, and what it still produces as a fallback for
    /// a frame carrying no <c>metadata.transcript</c>: every result's first alternative joined with a
    /// single space, unconditionally.
    /// </summary>
    private static string RecordedSpaceJoinedTranscript(string frame)
    {
        using var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(frame));
        var parts = document.RootElement.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("alternatives")[0].GetProperty("content").GetString()!);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// The recorded frame with its whole <c>metadata</c> object removed, so a test can reach the
    /// local-assembly fallback at all. Removing the object rather than blanking the string is
    /// deliberate: an absent field and an empty one are different instructions, and only the absent
    /// one is supposed to fall back.
    /// </summary>
    private static string FrameWithoutMetadata(string frame)
    {
        var node = JsonNode.Parse(SpeechmaticsFakeServer.ReadFrame(frame))!.AsObject();
        node.Remove("metadata");
        return node.ToJsonString();
    }

    /// <summary>The recorded frame with its language pack declaring <paramref name="delimiter"/>.</summary>
    private static string RecognitionStartedWithDelimiter(string delimiter)
    {
        var node = JsonNode.Parse(
            SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.RecognitionStartedFrame))!.AsObject();
        node["language_pack_info"]!["word_delimiter"] = delimiter;
        return node.ToJsonString();
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldRecordedTranscripts_WhenReplayingDocumentedFrames()
    {
        // The default seed is the two recorded frames verbatim: partial then final.
        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var partial = RecordedVendorTranscript(SpeechmaticsFakeServer.PartialTranscriptFrame);
        var final = RecordedVendorTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        partial.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");
        final.Should().NotBeNullOrWhiteSpace("a frame that transcribes to nothing asserts nothing");

        results.Should().HaveCount(2);
        results[0].IsFinal.Should().BeFalse();
        results[0].Transcript.Should().Be(partial);
        results[1].IsFinal.Should().BeTrue();
        results[1].Transcript.Should().Be(final);
    }

    [Fact]
    public async Task StreamAsync_ShouldTolerateUnmodelledSiblingFields_WhenFrameCarriesFullDocumentedFieldSet()
    {
        // The point of the recording. SpeechmaticsTranscriptMessage models three values — message,
        // and each result's first alternative's content and confidence — out of everything
        // Speechmatics documents. The first block fences the fixture: reduce it back to the old
        // message + results[].alternatives[] shape and this fails loudly instead of silently taking
        // the assertion below with it.
        using (var document = JsonDocument.Parse(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame)))
        {
            var root = document.RootElement;
            foreach (var unmodelled in new[] { "format", "metadata", "forced" })
            {
                root.TryGetProperty(unmodelled, out _)
                    .Should().BeTrue("the recorded frame must carry '{0}', which the SDK does not model", unmodelled);
            }

            var metadata = root.GetProperty("metadata");
            foreach (var metadataField in new[] { "start_time", "end_time", "transcript" })
            {
                metadata.TryGetProperty(metadataField, out _)
                    .Should().BeTrue("metadata must carry '{0}' — the SDK reads none of it", metadataField);
            }

            var results = root.GetProperty("results");
            results.GetArrayLength().Should().BeGreaterThan(1, "a single-token stream cannot expose the join behaviour");
            foreach (var resultField in new[] { "type", "start_time", "end_time", "is_eos" })
            {
                results[0].TryGetProperty(resultField, out _)
                    .Should().BeTrue("a recorded result must carry '{0}'", resultField);
            }

            var alternative = results[0].GetProperty("alternatives")[0];
            foreach (var alternativeField in new[] { "language", "display", "speaker", "tags" })
            {
                alternative.TryGetProperty(alternativeField, out _)
                    .Should().BeTrue("a recorded alternative must carry '{0}'", alternativeField);
            }

            // The punctuation token is load-bearing for the divergence test below.
            var last = results[results.GetArrayLength() - 1];
            last.GetProperty("type").GetString().Should().Be("punctuation");
            last.TryGetProperty("attaches_to", out _).Should().BeTrue();
        }

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var yielded = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        yielded.Should().ContainSingle()
            .Which.Transcript.Should().Be(RecordedVendorTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame));
    }

    /// <summary>
    /// The defect this suite pinned as behaviour until 2026-08-18, now inverted. The client
    /// space-joined every token unconditionally, so a punctuation result marked
    /// <c>attaches_to: "previous"</c> gained a separator the vendor's own text does not have.
    /// </summary>
    /// <remarks>
    /// The first block is the negative control and it is the reason this test can fail: it proves the
    /// recording still exercises the divergence. Swap the fixture for one whose tokens space-join to
    /// the same string and the assertion below would pass against a client that never read
    /// <c>metadata.transcript</c> at all.
    /// </remarks>
    [Fact]
    public async Task StreamAsync_ShouldYieldTheVendorsAssembledSegment_WhenFrameCarriesPunctuationAttachedToPrevious()
    {
        var vendorText = RecordedVendorTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        var spaceJoined = RecordedSpaceJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        spaceJoined.Should().NotBe(vendorText, "the recording must actually exercise the divergence");
        spaceJoined.Replace(" ", string.Empty).Should().Be(vendorText.Replace(" ", string.Empty),
            "the two differ only in whitespace — the tokens themselves agree");
        vendorText.Should().EndWith("mañana.", "the vendor attaches the full stop to the last word");
        spaceJoined.Should().EndWith("mañana .", "and the old join is what put a space in front of it");

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Transcript.Should().Be(vendorText);
    }

    /// <summary>
    /// The fallback, and the reason it is <em>use the vendor's delimiter</em> rather than
    /// <em>special-case punctuation</em>: a frame with no <c>metadata.transcript</c> is assembled with
    /// whatever <c>RecognitionStarted</c> declared, and a language pack declaring an empty
    /// <c>word_delimiter</c> assembles with no separators at all.
    /// </summary>
    /// <remarks>
    /// A rule that hard-coded a space and only suppressed it before punctuation would pass the test
    /// above and fail this one, which is exactly why this one exists. Note also what it fences on the
    /// other side: <c>attaches_to</c> is still honoured here, so the empty delimiter is not doing the
    /// work alone.
    /// </remarks>
    [Fact]
    public async Task StreamAsync_ShouldAssembleWithTheDeclaredDelimiter_WhenFrameCarriesNoVendorTranscript()
    {
        var tokens = RecordedSpaceJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame)
            .Replace(" ", string.Empty);

        _server.RecognitionStartedJson = RecognitionStartedWithDelimiter(string.Empty);
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(FrameWithoutMetadata(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Transcript.Should().Be(tokens);
    }

    /// <summary>
    /// The fallback still honours the language pack's declared delimiter when it is an ordinary
    /// space — and, doing so, produces exactly the vendor's own text. That agreement is the fence:
    /// the fallback cannot drift away from the authority it stands in for without this going red.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldAssembleWhatTheVendorWouldHave_WhenFrameCarriesNoVendorTranscript()
    {
        var vendorText = RecordedVendorTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(FrameWithoutMetadata(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Transcript.Should().Be(vendorText);
    }

    /// <summary>
    /// Pins the authority rule itself: when the vendor's assembled segment and any local assembly of
    /// the same tokens disagree, the vendor's text is what the caller receives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The divergence here is constructed, and no measured frame contains one.</strong> Probed
    /// live on 2026-08-18 over eleven transcript messages, the vendor's trimmed <c>metadata.transcript</c>
    /// and the delimiter-and-<c>attaches_to</c> assembly agreed character for character every time — so
    /// this frame's <c>metadata.transcript</c> is patched to a string its own tokens cannot produce.
    /// Saying that plainly matters: a reader must not take this test as evidence that Speechmatics
    /// rewrites segments, only that if it ever does, its answer wins.
    /// </para>
    /// <para>
    /// It exists because without it nothing failed when the vendor's text was ignored altogether. That
    /// was measured too: reverting the client to always assemble locally left all twenty tests in this
    /// class green, because on the committed fixtures the two sources agree by construction. The rule
    /// this change decided (§4.10) was therefore unobservable, which is the same silent-pass shape this
    /// work exists to remove. Inverse text normalisation, casing and writing direction are all places
    /// the vendor knows something the token stream does not carry, and that is what the rule is for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StreamAsync_ShouldPreferTheVendorsTranscript_WhenItDisagreesWithLocalAssembly()
    {
        const string VendorRewrote = "El equipo revisó el informe a las 9:30.";
        var node = JsonNode.Parse(
            SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame))!.AsObject();
        node["metadata"]!["transcript"] = VendorRewrote;

        var localAssembly = RecordedSpaceJoinedTranscript(SpeechmaticsFakeServer.FinalTranscriptFrame);
        VendorRewrote.Should().NotBe(localAssembly);
        VendorRewrote.Replace(" ", string.Empty).Should().NotBe(localAssembly.Replace(" ", string.Empty),
            "the two must differ in more than whitespace, or no assembly rule could tell them apart");

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(node.ToJsonString());

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Transcript.Should().Be(VendorRewrote);
    }

    /// <summary>
    /// Confidence keeps coming from <c>alternatives[0].confidence</c> even though the text no longer
    /// comes from the same walk (§4.11). The two used to be produced by one loop; this pins that
    /// separating them did not quietly change what the published number means.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldAverageAlternativeConfidences_WhenTextComesFromTheVendorsTranscript()
    {
        using var document = JsonDocument.Parse(
            SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));
        var confidences = document.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("alternatives")[0].GetProperty("confidence").GetSingle())
            .ToArray();
        var expected = confidences.Average();

        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.ReadFrame(SpeechmaticsFakeServer.FinalTranscriptFrame));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle().Which.Confidence.Should().BeApproximately(expected, 0.0001f);
    }

    [Fact]
    public async Task StreamAsync_ShouldSendStartRecognition_WithAudioAndTranscriptionConfig()
    {
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o =>
        {
            o.Language = "es";
            o.OperatingPoint = "enhanced";
            o.EnablePartials = true;
            o.MaxDelaySeconds = 2;
            o.SampleRate = 16000;
        });

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedStartRecognitionJson.Should().NotBeNullOrEmpty();
        var json = _server.ReceivedStartRecognitionJson!;
        json.Should().Contain("\"message\":\"StartRecognition\"");
        json.Should().Contain("\"encoding\":\"pcm_s16le\"");
        json.Should().Contain("\"language\":\"es\"");
        json.Should().Contain("\"operating_point\":\"enhanced\"");
        json.Should().Contain("\"enable_partials\":true");
        // The request-target is the language pack and nothing else — the credential is a header now.
        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().EndWith("/v2/es");
    }

    [Fact]
    public async Task StreamAsync_ShouldAuthenticateWithABearerHeader_WhenOpeningTheSession()
    {
        // The defect this replaces: the key travelled as ?jwt=, which the service answers by
        // upgrading (101) and then closing 4001 not_authorised — so the whole provider was unusable
        // and this suite was green anyway, because the fake never looked at the credential.
        // Measured 2026-08-15: the same key in this header reached RecognitionStarted.
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer();

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedAuthorizationHeader.Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldKeepTheCredentialOutOfTheUrl_WhenOpeningTheSession()
    {
        // Separate from the header assertion on purpose: sending the header while still sending
        // ?jwt= would satisfy that one and leave the long-lived key in a request-target that lands
        // in every proxy and access log along the way.
        _server.ResultMessages.Clear();
        var recognizer = BuildRecognizer(o => o.ApiKey = "sk-not-a-real-key");

        await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedRequestUri.Should().NotBeNullOrEmpty();
        _server.ReceivedRequestUri!.Should().NotContain("jwt");
        _server.ReceivedRequestUri!.Should().NotContain("sk-not-a-real-key");
        _server.ReceivedAuthorizationHeader.Should().Be("Bearer sk-not-a-real-key");
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldPartialTranscript_WhenAddPartialTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hola", 0.80f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola");
        results[0].IsFinal.Should().BeFalse();
        results[0].Confidence.Should().BeApproximately(0.80f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldFinalTranscript_WhenAddTranscript()
    {
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.99f));

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().ContainSingle();
        results[0].Transcript.Should().Be("hola mundo");
        results[0].IsFinal.Should().BeTrue();
        results[0].Confidence.Should().BeApproximately(0.99f, 0.01f);
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreLifecycleMessages_WhenEndOfTranscript()
    {
        // Server sends RecognitionStarted automatically. Add only EndOfTranscript — no
        // AddPartialTranscript / AddTranscript — so we expect zero yielded results. Both lifecycle
        // frames are now the recorded ones, so the message filter is exercised against the shapes
        // Speechmatics documents — including RecognitionStarted's nested language_pack_info —
        // rather than against two-field placeholders.
        //
        // This is also the guard on the recognition half of ADR-0050 E5, and the reason that half is
        // deliberately not the synthesis rule: a session that produced no transcript is a *healthy*
        // session (turn detection flushes on any trigger, so noise with no speech correctly yields
        // nothing). Zero results must therefore stay an empty list, never an exception — what E5
        // reports on this surface is a session with no vendor messages at all, which the test below
        // drives.
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildEndOfTranscriptJson());

        var recognizer = BuildRecognizer();
        var results = await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        results.Should().BeEmpty();
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c), and the inverse of what this test used to assert. Under
    /// <c>NotThrowAsync</c> a socket killed mid-session ended the transcript stream exactly as the
    /// vendor's own <c>EndOfTranscript</c> does — the caller could not tell a truncated transcript
    /// from a complete one, which on this surface is the difference between a whole utterance and half
    /// of one.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession()
    {
        _server.AbortAfterSend = true;
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Code.Should().BeNull("a dead socket carries no vendor code");
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and the one this vendor makes the case for: it was measured accepting the
    /// upgrade with <c>101</c> and only then rejecting the credential with close <c>4001
    /// not_authorised</c> — so on this surface the handshake succeeds and the close code carries the
    /// failure. <c>ADR-0050</c> E7 exists so a caller does not have to know which of the two a vendor
    /// picked. Here the upgrade is refused outright: no HTTP answer, hence no code.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var recognizer = BuildRecognizer(o => o.BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/v2");

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// Door 1 (<c>ADR-0050</c> E2a) with this vendor's inverted naming: <c>message</c> is the kind and
    /// <c>type</c> is the code, so the code that reaches
    /// <see cref="SpeechProviderFailureException.Code"/> is a symbol rather than a number. The fake
    /// closes normally afterwards, so the frame is the only failure signal in the session.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailure_WhenTheServerSendsAnErrorMessage()
    {
        _server.ErrorFrameJson =
            """{"message":"Error","type":"not_authorised","reason":"Not authorised for this endpoint"}""";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("not_authorised", "on this surface `type` is the code, not the kind");
        failure.Message.Should().Contain("Not authorised for this endpoint");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b), and the measured failure this whole change started from: a
    /// rejected credential here is <c>101</c> followed by close <c>4001 not_authorised</c> and nothing
    /// else. There is no frame to read — the code is the entire evidence — and it was read nowhere,
    /// which is why the provider was unusable as shipped while every test stayed green.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowCloseCodeFailure_WhenCredentialIsRejectedWithACloseCode()
    {
        _server.EndSessionSilently = true;
        _server.CloseStatus = (WebSocketCloseStatus)4001;
        _server.CloseStatusDescription = "not_authorised";
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("4001");
        failure.Message.Should().Contain("not_authorised");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5) on the recognition side: the vendor accepted the upgrade, sent no
    /// message of any kind — not even the <c>RecognitionStarted</c> greeting — and closed normally.
    /// Nothing failed on the wire, so this is not a <see cref="SpeechProviderFailureException"/>; it is
    /// also not the healthy zero-transcript session above, and the two must not report the same way.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowEmptyResult_WhenTheVendorSendsNoMessageAtAll()
    {
        _server.EndSessionSilently = true;
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "the session was clean — it was simply silent");
        empty.Provider.Should().Be("Speechmatics");
    }

    [Fact]
    public async Task StreamAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence): a pre-cancelled token throws
        // OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/mock latency. No wall-clock race against the
        // fake server (see openspec/changes/archive/2026-07-05-stt-cancellation-test-fence).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var recognizer = BuildRecognizer();

        var act = async () =>
            await recognizer.StreamAsync(
                    SttFrameGenerators.EndlessFrames(), AudioFormat.Slin16Mono8kHz, cts.Token)
                // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
                // makes the enumerator throw on our behalf, and the assertion then cannot tell a
                // propagated throw from a silent `yield break` in the subject.
                .ToListAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _server.ReceivedFrameCount.Should().Be(0);
    }

    /// <summary>
    /// <c>last_seq_no</c> is compared against the count the fake kept independently, not against a
    /// literal: the client and the server have to agree on how many audio chunks crossed the
    /// socket, and a hard-coded 3 would still pass if both drifted together.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldSendEndOfStreamNumberedWithTheAudioChunkCount_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        using var terminator = JsonDocument.Parse(_server.ReceivedEndOfStreamJson!);
        terminator.RootElement.GetProperty("message").GetString().Should().Be("EndOfStream");
        terminator.RootElement.GetProperty("last_seq_no").GetInt32()
            .Should().Be(_server.ReceivedFrameCount).And.Be(3);
    }

    /// <summary>
    /// The load-bearing half of the pair. §3.6d streamed one utterance of ten spoken digits into
    /// this surface three ways: half-close alone returned 0/10 digits — twenty
    /// <c>AddPartialTranscript</c> messages, not one <c>AddTranscript</c>, and no
    /// <c>EndOfTranscript</c> — the terminator alone returned 10/10, and sending both returned 0/10
    /// again. So the half-close is not merely redundant here, it destroys the result even when the
    /// terminator precedes it. This test fails the moment a client sends it again.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds()
    {
        var recognizer = BuildRecognizer();
        await recognizer.StreamAsync(ThreeFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await SessionEndedAsync();

        _server.ReceivedClientCloseFrame.Should().BeFalse();
    }

    /// <summary>
    /// Wait for the fake's session handler to return before asserting on what the client sent last.
    /// <c>StreamAsync</c> returns as soon as the server closes, which can be before the server has
    /// read the frames the client sent just before that — so without this join point a half-close
    /// assertion is a race the defect wins. The bound is a liveness guard, not a synchronisation
    /// delay: it is never reached on a passing run.
    /// </summary>
    private Task SessionEndedAsync() => _server.SessionCompleted.WaitAsync(TimeSpan.FromSeconds(10));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ThreeFrames()
    {
        for (int i = 0; i < 3; i++) yield return new byte[320];
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
