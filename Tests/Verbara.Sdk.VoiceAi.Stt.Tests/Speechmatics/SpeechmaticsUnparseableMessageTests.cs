using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// A Speechmatics message the recognizer cannot read costs that message, never the session: one whose
/// envelope says <c>"message": "Error"</c> is the provider failure a readable error message raises,
/// carrying the raw message and the code from <c>type</c>; anything else unreadable is counted on
/// <c>stt.frames.unparseable</c>, tagged <c>voiceai.provider=Speechmatics</c>, and skipped. Both decode
/// passes are covered: the transcript decode and the second decode of the <c>RecognitionStarted</c>
/// greeting.
/// </summary>
/// <remarks>
/// The counter is read after the stream ends, never on a signal from the fake (see
/// <see cref="SttUnparseableFrames"/>); the class is in <see cref="SttProcessWideCounters"/> because the
/// counter is process-wide.
/// </remarks>
[Collection(SttProcessWideCounters.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SpeechmaticsUnparseableMessageTests : IAsyncLifetime
{
    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A final transcript whose alternative's <c>confidence</c> is <c>null</c>: it fails the transcript
    /// decode, and its envelope names no failure.
    /// </summary>
    private static readonly string NullConfidenceTranscript = WithNullConfidence(
        SpeechmaticsFakeServer.BuildFinalTranscriptJson("perdido", 0.9f));

    /// <summary>
    /// The recorded greeting with <c>language_pack_info.word_delimiter</c> sent as a number: it passes the
    /// transcript decode, which does not read that member, and fails the greeting's own second decode.
    /// </summary>
    private static readonly string NumericDelimiterGreeting = WithNumericWordDelimiter(
        SpeechmaticsFakeServer.BuildRecognitionStartedJson());

    /// <summary>An <c>Error</c> message whose <c>reason</c> is an object: it fails the decode, and its envelope names the failure.</summary>
    private const string UnreadableErrorMessage =
        """{"message":"Error","type":"not_authorised","reason":{"detail":"Not authorised for this endpoint"}}""";

    private readonly SpeechmaticsFakeServer _server = new();
    private readonly SttUnparseableFrames _unparseable = new("Speechmatics");

    public SpeechmaticsUnparseableMessageTests() => _server.Start();

    /// <summary>
    /// (a) A transcript that fails to deserialize and names no failure, then a good one: no fault, the good
    /// result delivered, the unreadable one counted once with the provider named.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenAMessageFailsToDeserializeAndNamesNoFailure()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullConfidenceTranscript);
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Count.Should().Be(1, "the unreadable message is counted once, tagged voiceai.provider=Speechmatics");
    }

    /// <summary>
    /// (a′) The greeting passes the first decode and fails the second (its delimiter is a number): it names
    /// no failure, so it is counted and skipped, and the transcript after it is delivered.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenTheGreetingFailsItsSecondDecode()
    {
        // Arrange
        _server.RecognitionStartedJson = NumericDelimiterGreeting;
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Count.Should().Be(1, "the greeting the second decode cannot read is counted once");
    }

    /// <summary>
    /// (b) An <c>Error</c> message the recognizer cannot read is the failure a readable one raises: the
    /// error-frame signal, the code from <c>type</c> and the raw message in the exception's message.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailureCarryingTheRawMessage_WhenAnErrorMessageCannotBeRead()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorMessage;

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("not_authorised", "on this surface `type` is the code, read from the envelope");
        failure.Message.Should().Contain(UnreadableErrorMessage, "the member that failed may be the one holding the vendor's text");
        _unparseable.Count.Should().Be(0, "a named failure is reported, not counted");
    }

    /// <summary>(c) The unreadable <c>Error</c> message of (b), sent in 16-byte fragments: the same failure, judged on the whole message.</summary>
    [Fact]
    public async Task StreamAsync_ShouldThrowErrorFrameFailureCarryingTheRawMessage_WhenAnUnreadableErrorMessageArrivesInFragments()
    {
        // Arrange
        _server.ErrorFrameJson = UnreadableErrorMessage;
        _server.TextFrameFragmentBytes = 16;

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("not_authorised");
        failure.Message.Should().Contain(UnreadableErrorMessage);
        _unparseable.Count.Should().Be(0);
    }

    /// <summary>(d) A text message that is not JSON at all names no failure: counted, skipped, the transcript after it delivered.</summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenATextMessageIsNotJson()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add("upstream timeout, retry later");
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Count.Should().Be(1);
    }

    /// <summary>
    /// (e) A session whose every message was skipped — the greeting, a transcript and the reply to the
    /// end of input — heard the vendor: it completes with no results and no exception, not the
    /// empty-session failure.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCompleteWithNoResults_WhenEveryMessageTheVendorSentWasSkipped()
    {
        // Arrange
        _server.RecognitionStartedJson = "not json";
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullConfidenceTranscript);
        _server.EndOfTranscriptJson = "not json either";

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var results = (await act.Should().NotThrowAsync()).Subject;
        results.Should().BeEmpty();
        _unparseable.Count.Should().Be(3);
    }

    /// <summary>(f) Control: readable messages only — green on the unfixed code, and the counter does not move.</summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldEveryResultAndCountNothing_WhenEveryMessageIsReadable()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hola", 0.8f));
        _server.ResultMessages.Add(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hola mundo", 0.95f));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Select(r => r.Transcript).Should().Equal("hola", "hola mundo");
        _unparseable.Count.Should().Be(0);
    }

    private SpeechmaticsSpeechRecognizer BuildRecognizer() => new(Options.Create(new SpeechmaticsOptions
    {
        ApiKey = "test-key",
        BaseUri = $"ws://127.0.0.1:{_server.Port}/v2",
    }));

    private static string WithNullConfidence(string frame)
    {
        var node = JsonNode.Parse(frame)!;
        node["results"]![0]!["alternatives"]![0]!["confidence"] = null;
        return node.ToJsonString();
    }

    private static string WithNumericWordDelimiter(string frame)
    {
        var node = JsonNode.Parse(frame)!;
        node["language_pack_info"]!["word_delimiter"] = 42;
        return node.ToJsonString();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleFrame()
    {
        yield return new byte[320];
        await Task.CompletedTask;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        _unparseable.Dispose();
        await _server.DisposeAsync();
    }
}
