using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Lmnt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;

/// <summary>
/// An LMNT text message the synthesizer cannot read costs that message, never the synthesis: one whose
/// envelope carries an <c>error</c> member that is present, not null and not empty — or says
/// <c>"type": "error"</c> — is the provider failure a readable one raises, carrying the raw message;
/// anything else unreadable is counted on <c>tts.frames.unparseable</c>, tagged
/// <c>voiceai.provider=LMNT</c>, and skipped, and the binary audio around it is delivered.
/// </summary>
/// <remarks>
/// The counter is read after the synthesis ends, never on a signal from the fake (see
/// <see cref="TtsUnparseableFrames"/>); the class is in <see cref="TtsProcessWideCounters"/> because the
/// counter is process-wide. Audio arrives as binary frames of the recorded tone, whose length is not a
/// multiple of the fake's 320-byte frame, so "the audio around it arrives whole" is asserted byte for byte.
/// </remarks>
[Collection(TtsProcessWideCounters.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class LmntUnparseableMessageTests : IAsyncLifetime
{
    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A <c>buffer_empty</c> notification whose <c>message</c> is a number: it fails to deserialize, and its
    /// envelope names no failure.
    /// </summary>
    private const string UnreadableMessage = """{"type":"buffer_empty","message":42}""";

    /// <summary>
    /// The vendor's failure frame in its bare shape — an <c>error</c> member and no <c>type</c> — with the
    /// <c>error</c> sent as an object: it fails to deserialize, and only the <c>error</c>-member test names
    /// the failure, the case a <c>type</c>-only peek misses.
    /// </summary>
    private const string UnreadableErrorFrame = """{"error":{"reason":"Invalid API key"}}""";

    private readonly LmntWsFakeServer _server = new();
    private readonly TtsUnparseableFrames _unparseable = new("LMNT");

    public LmntUnparseableMessageTests() => _server.Start();

    private static byte[] RecordedAudio => LmntWsFakeServer.ReadFrameBytes(LmntWsFakeServer.AudioChunk);

    /// <summary>
    /// (a) A text message that fails to deserialize and names no failure, between two binary audio frames:
    /// no fault, every byte of the tone delivered, the unreadable message counted once with the provider named.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldCountSkipAndDeliverTheAudioAroundIt_WhenAMessageFailsToDeserializeAndNamesNoFailure()
    {
        // Arrange
        _server.TextFramesAmongAudio.Add((1, UnreadableMessage));

        // Act
        var frames = await Synthesize();

        // Assert
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio, "the audio before and after the skipped message arrives whole");
        _unparseable.Count.Should().Be(1, "the unreadable message is counted once, tagged voiceai.provider=LMNT");
    }

    /// <summary>
    /// (b) A failure frame the synthesizer cannot read, in the bare <c>error</c>-member shape, is the
    /// failure a readable one raises: the error-frame signal and the raw frame in the exception's message.
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
        failure.Code.Should().BeNull("the readable branch reads no code on this vendor, and neither does the peek");
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
        failure.Code.Should().BeNull();
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
    /// (e) A session whose every message was skipped — an unreadable message and an unreadable terminator —
    /// produced no audio: it ends with the empty-result exception, by the rule that already exists.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenEveryMessageTheVendorSentWasSkipped()
    {
        // Arrange
        _server.AudioFramesToSend.Clear();
        _server.TextFramesAmongAudio.Add((0, UnreadableMessage));
        _server.FinishFrameJson = "not json either";

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

    private async Task<List<ReadOnlyMemory<byte>>> Synthesize() =>
        await BuildSynthesizer().SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

    private LmntSpeechSynthesizer BuildSynthesizer() => new(
        Options.Create(new LmntTtsOptions
        {
            ApiKey = "test-lmnt-key",
            Voice = LmntVoices.Leah,
            Transport = LmntTransport.WebSocket,
        }),
        fakeWsPort: _server.Port);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _unparseable.Dispose();
        await _server.DisposeAsync();
    }
}
