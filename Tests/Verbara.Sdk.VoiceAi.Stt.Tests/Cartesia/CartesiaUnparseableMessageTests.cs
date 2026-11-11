using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Cartesia;
using Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>
/// A Cartesia message the recognizer cannot read costs that message, never the session: one whose
/// envelope says <c>"type": "error"</c> is the provider failure a readable error message raises, carrying
/// the raw message and the code from <c>code</c>; anything else unreadable is counted on
/// <c>stt.frames.unparseable</c>, tagged <c>voiceai.provider=Cartesia</c>, and skipped.
/// </summary>
/// <remarks>
/// The counter is read after the stream ends, never on a signal from the fake (see
/// <see cref="SttUnparseableFrames"/>); the class is in <see cref="SttProcessWideCounters"/> because the
/// counter is process-wide.
/// </remarks>
[Collection(SttProcessWideCounters.Name)]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CartesiaUnparseableMessageTests : IAsyncLifetime
{
    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A final transcript whose <c>is_final</c> is <c>null</c>: it fails to deserialize, and its envelope
    /// names no failure.
    /// </summary>
    private static readonly string NullIsFinal = WithNullIsFinal(
        CartesiaFakeServer.BuildTranscriptJson("perdido", 0.9f, isFinal: true));

    /// <summary>
    /// An <c>error</c> message in the recorded shape with its <c>code</c> sent as a string: it fails to
    /// deserialize, and its envelope names the failure.
    /// </summary>
    private const string UnreadableErrorMessage =
        """{"type":"error","code":"400","message":"Missing sample_rate: the query string carries none"}""";

    private readonly CartesiaFakeServer _server = new();
    private readonly SttUnparseableFrames _unparseable = new("Cartesia");

    public CartesiaUnparseableMessageTests() => _server.Start();

    /// <summary>
    /// (a) A transcript that fails to deserialize and names no failure, then a good one: no fault, the good
    /// result delivered, the unreadable one counted once with the provider named.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCountSkipAndDeliverTheNextResult_WhenAMessageFailsToDeserializeAndNamesNoFailure()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullIsFinal);
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Count.Should().Be(1, "the unreadable message is counted once, tagged voiceai.provider=Cartesia");
    }

    /// <summary>
    /// (b) An <c>Error</c> message the recognizer cannot read is the failure a readable one raises: the
    /// error-frame signal, the code from <c>code</c> and the raw message in the exception's message.
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
        failure.Code.Should().Be("400", "the code is read from the envelope where it is a string");
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
        failure.Code.Should().Be("400");
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
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Should().ContainSingle().Which.Transcript.Should().Be("hola mundo");
        _unparseable.Count.Should().Be(1);
    }

    /// <summary>
    /// (e) A session whose every message was skipped — a transcript and the reply to the end of input —
    /// heard the vendor: it completes with no results and no exception, not the
    /// empty-session failure.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ShouldCompleteWithNoResults_WhenEveryMessageTheVendorSentWasSkipped()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(NullIsFinal);
        _server.DoneJson = "not json either";

        // Act
        var act = async () => await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        var results = (await act.Should().NotThrowAsync()).Subject;
        results.Should().BeEmpty();
        _unparseable.Count.Should().Be(2);
    }

    /// <summary>(f) Control: readable messages only — green on the unfixed code, and the counter does not move.</summary>
    [Fact]
    public async Task StreamAsync_ShouldYieldEveryResultAndCountNothing_WhenEveryMessageIsReadable()
    {
        // Arrange
        _server.ResultMessages.Clear();
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola", 0.8f, isFinal: false));
        _server.ResultMessages.Add(CartesiaFakeServer.BuildTranscriptJson("hola mundo", 0.95f, isFinal: true));

        // Act
        var results = await BuildRecognizer().StreamAsync(SingleFrame(), AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Assert
        results.Select(r => r.Transcript).Should().Equal("hola", "hola mundo");
        _unparseable.Count.Should().Be(0);
    }

    private CartesiaSpeechRecognizer BuildRecognizer() => new(Options.Create(new CartesiaOptions
    {
        ApiKey = "test-key",
        BaseUri = $"ws://127.0.0.1:{_server.Port}/stt/websocket",
    }));

    private static string WithNullIsFinal(string frame)
    {
        var node = JsonNode.Parse(frame)!;
        node["is_final"] = null;
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
