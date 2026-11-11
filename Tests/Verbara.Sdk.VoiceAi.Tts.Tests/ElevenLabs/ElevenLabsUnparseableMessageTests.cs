using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

/// <summary>
/// An ElevenLabs message the synthesizer cannot read costs that message, never the synthesis: one whose
/// envelope carries a bare <c>error</c> member — this vendor's failure frame has no discriminator — is the
/// provider failure a readable failure frame raises, carrying the raw message and the code from
/// <c>error</c>; anything else unreadable — a frame whose <c>audio</c> is not valid base64 included — is
/// counted on <c>tts.frames.unparseable</c>, tagged <c>voiceai.provider=ElevenLabs</c>, and skipped, and
/// the audio around it is delivered.
/// </summary>
/// <remarks>
/// The counter is read after the synthesis ends, never on a signal from the fake (see
/// <see cref="TtsUnparseableFrames"/>); the class is in <see cref="TtsProcessWideCounters"/> because the
/// counter is process-wide. The recorded tone is 2,808 bytes, deliberately not a multiple of the fake's
/// 320-byte frame, so "the audio around it arrives whole" is asserted byte for byte.
/// </remarks>
[Collection(TtsProcessWideCounters.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class ElevenLabsUnparseableMessageTests : IAsyncLifetime
{
    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An audio frame whose <c>isFinal</c> is a string: it fails to deserialize, and its envelope carries no
    /// <c>error</c> member.
    /// </summary>
    private const string UnreadableFrame = """{"audio":null,"isFinal":"false"}""";

    /// <summary>
    /// The vendor's failure frame — a bare <c>error</c> member, no discriminator — with its <c>message</c>
    /// sent as an object: it fails to deserialize, and its envelope names the failure.
    /// </summary>
    private const string UnreadableErrorFrame =
        """{"message":{"detail":"Invalid API key: the key is not recognised"},"error":"invalid_api_key","code":1008}""";

    /// <summary>An audio frame that parses and names no failure, but whose <c>audio</c> is not valid base64.</summary>
    private const string NotBase64Frame = """{"audio":"invalid base64!","isFinal":false}""";

    private readonly ElevenLabsFakeServer _server = new();
    private readonly TtsUnparseableFrames _unparseable = new("ElevenLabs");

    public ElevenLabsUnparseableMessageTests() => _server.Start();

    private static byte[] RecordedAudio => ElevenLabsFakeServer.ReadFrameBytes(ElevenLabsFakeServer.AudioChunk);

    /// <summary>
    /// (a) A message that fails to deserialize and names no failure, between two audio frames: no fault,
    /// every byte of the tone delivered, the unreadable message counted once with the provider named.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldCountSkipAndDeliverTheAudioAroundIt_WhenAMessageFailsToDeserializeAndNamesNoFailure()
    {
        // Arrange
        _server.TextFramesAmongAudio.Add((1, UnreadableFrame));

        // Act
        var frames = await Synthesize();

        // Assert
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio, "the audio before and after the skipped message arrives whole");
        _unparseable.Count.Should().Be(1, "the unreadable message is counted once, tagged voiceai.provider=ElevenLabs");
    }

    /// <summary>
    /// (b) A failure frame the synthesizer cannot read, in the bare <c>error</c>-member shape a
    /// <c>type</c>-only peek misses, is the failure a readable one raises: the error-frame signal, the code
    /// from <c>error</c> and the raw frame in the exception's message.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailureCarryingTheRawFrame_WhenAnErrorFrameCannotBeRead()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorFrame;

        // Act
        var act = Synthesize;

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("invalid_api_key", "the code is read from error, as the readable branch reads it");
        failure.Message.Should().Contain(UnreadableErrorFrame, "the member that failed may be the one holding the vendor's text");
        _unparseable.Count.Should().Be(0, "a named failure is reported, not counted");
    }

    /// <summary>(c) The unreadable failure frame of (b), sent in 16-byte fragments: the same failure, judged on the whole message.</summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailureCarryingTheRawFrame_WhenAnUnreadableErrorFrameArrivesInFragments()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorFrame;
        _server.TextFrameFragmentBytes = 16;

        // Act
        var act = Synthesize;

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("invalid_api_key");
        failure.Message.Should().Contain(UnreadableErrorFrame);
        _unparseable.Count.Should().Be(0);
    }

    /// <summary>(d) A text message that is not JSON at all names no failure: counted, skipped, the audio around it delivered.</summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldCountSkipAndDeliverTheAudioAroundIt_WhenATextMessageIsNotJson()
    {
        // Arrange
        _server.TextFramesAmongAudio.Add((1, "upstream timeout, retry later"));

        // Act
        var frames = await Synthesize();

        // Assert
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
        _unparseable.Count.Should().Be(1);
    }

    /// <summary>
    /// (e) A session whose every message was skipped — an unreadable frame and a message that is not JSON —
    /// produced no audio: it ends with the empty-result exception, by the rule that already exists.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenEveryMessageTheVendorSentWasSkipped()
    {
        // Arrange
        _server.AudioFramesToSend.Clear();
        _server.TextFramesAmongAudio.Add((0, UnreadableFrame));
        _server.TextFramesAmongAudio.Add((0, "not json either"));

        // Act
        var act = Synthesize;

        // Assert
        await act.Should().ThrowAsync<SpeechProviderEmptyResultException>();
        _unparseable.Count.Should().Be(2);
    }

    /// <summary>(f) Control: readable messages only — green on the unfixed code, and the counter does not move.</summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldDeliverEveryByteAndCountNothing_WhenEveryMessageIsReadable()
    {
        // Act
        var frames = await Synthesize();

        // Assert
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
        _unparseable.Count.Should().Be(0);
    }

    /// <summary>
    /// (a′) A text frame whose <c>audio</c> is not valid base64, between two readable ones: no fault, every
    /// byte of the tone delivered, nothing written for the bad frame, and the frame counted once.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldCountSkipAndDeliverTheAudioAroundIt_WhenATextFrameCarriesAudioThatIsNotBase64()
    {
        // Arrange
        _server.TextFramesAmongAudio.Add((1, NotBase64Frame));

        // Act
        var frames = await Synthesize();

        // Assert
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio, "nothing is written for the frame whose audio does not decode");
        _unparseable.Count.Should().Be(1, "audio that does not decode is an unparseable message, tagged voiceai.provider=ElevenLabs");
    }

    /// <summary>
    /// (e′) A session whose every audio frame carries audio that is not valid base64: no audio, so the
    /// empty-result exception, and each of the three frames counted.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenEveryTextFrameCarriesAudioThatIsNotBase64()
    {
        // Arrange
        _server.AudioFramesToSend.Clear();
        _server.TextFramesAmongAudio.Add((0, NotBase64Frame));
        _server.TextFramesAmongAudio.Add((0, NotBase64Frame));
        _server.TextFramesAmongAudio.Add((0, NotBase64Frame));

        // Act
        var act = Synthesize;

        // Assert
        await act.Should().ThrowAsync<SpeechProviderEmptyResultException>();
        _unparseable.Count.Should().Be(3, "every frame whose audio did not decode is counted");
    }

    private async Task<List<ReadOnlyMemory<byte>>> Synthesize() =>
        await BuildSynthesizer().SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

    private ElevenLabsSpeechSynthesizer BuildSynthesizer() => new(Options.Create(new ElevenLabsOptions
    {
        ApiKey = "test-key",
        VoiceId = "test-voice",
        BaseUri = $"ws://127.0.0.1:{_server.Port}/v1/text-to-speech",
    }));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _unparseable.Dispose();
        await _server.DisposeAsync();
    }
}
