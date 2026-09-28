using System.Text.Json;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// What the bridge puts on the wire when it opens a session: the upgrade request, and the
/// <c>session.update</c> it sends first. Each test reads what the fake captured.
/// </summary>
/// <remarks>
/// <para>
/// The fake accepts any shape, so it cannot refuse a session the way the vendor does; these tests pin
/// the shape the live endpoint accepted instead (measured 2026-09-27). The endpoint closed a session
/// that opted into the retired beta with <c>4000 beta_api_shape_disabled</c>, and without the opt-in
/// it refused the beta <c>session.update</c> one member at a time: <c>session.type</c> missing, then
/// <c>session.voice</c>, <c>session.modalities</c>, <c>session.turn_detection</c> and
/// <c>session.input_audio_format</c> unknown. A suite that only checked that a
/// <c>session.update</c> was sent stayed green through all of it.
/// </para>
/// <para>
/// Each session is ended by cancelling its token once the fake has the frame under assertion and the
/// loops are running, the way <see cref="OpenAiRealtimeBridgeTests"/> ends its sessions.
/// </para>
/// </remarks>
public sealed class OpenAiRealtimeBridgeWireTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An event the fake delivers so that the bridge publishes something once its output loop runs.
    /// Never asserted on; see <c>OpenAiRealtimeBridgeTests.LoopsRunningMarkerEvent</c>.
    /// </summary>
    private const string LoopsRunningMarkerEvent = """{"type":"input_audio_buffer.speech_started"}""";

    private const string ApiKey = "test-key";

    /// <summary>
    /// The audio format the bridge's resamplers are built for, written for both directions: PCM at
    /// 24000 Hz. The vendor's defaults match today, and writing it turns a change of default into a
    /// refused <c>session.update</c> instead of audio played at the wrong rate.
    /// </summary>
    private const string PcmAt24kHz = """{"type":"audio/pcm","rate":24000}""";

    /// <summary>The beta session's top-level members, each of which the endpoint refuses as unknown.</summary>
    private static readonly string[] RetiredTopLevelMembers =
        ["voice", "modalities", "turn_detection", "input_audio_format", "output_audio_format"];

    [Fact]
    public async Task HandleSessionAsync_ShouldSendNoBetaHeader_WhenItOpensASession()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();

        // Act
        await OpenOneSessionAsync(fakeOpenAi, StartingOptions());

        // Assert
        fakeOpenAi.UpgradeHeaders.Should().ContainKey("Authorization")
            .WhoseValue.Should().Be($"Bearer {ApiKey}", "the credential travels as a bearer token");
        fakeOpenAi.UpgradeHeaders.Should().NotContainKey(
            "OpenAI-Beta",
            "the endpoint closes a session that opts into the retired beta with 4000 beta_api_shape_disabled");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldSendTheGaSessionShape_WhenVadIsServerSide()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        var options = StartingOptions();
        options.Voice = "marin";
        options.Instructions = "Answer in one short sentence.";
        options.VadMode = VadMode.ServerSide;
        var tool = new LookUpOrderFunction();

        // Act
        await OpenOneSessionAsync(fakeOpenAi, options, tool);

        // Assert
        using var update = SessionUpdateOf(fakeOpenAi);
        var session = Member(update.RootElement, "", "session");

        Member(session, "session", "type").GetString().Should().Be(
            "realtime", "the endpoint refuses a session.update without session.type");
        Member(session, "session", "output_modalities").EnumerateArray().Select(e => e.GetString())
            .Should().ContainSingle("the endpoint accepts only [\"audio\"] or [\"text\"], and only audio reaches the caller")
            .Which.Should().Be("audio");
        Member(session, "session", "instructions").GetString().Should().Be(options.Instructions);

        var audio = Member(session, "session", "audio");
        var input = Member(audio, "session.audio", "input");
        var output = Member(audio, "session.audio", "output");

        ShouldBeJson(Member(input, "session.audio.input", "format"), PcmAt24kHz, "session.audio.input.format");
        ShouldBeJson(Member(output, "session.audio.output", "format"), PcmAt24kHz, "session.audio.output.format");
        Member(output, "session.audio.output", "voice").GetString().Should().Be(
            "marin", "the voice is the configured one, under the audio output");
        var turnDetection = Member(input, "session.audio.input", "turn_detection");
        Member(turnDetection, "session.audio.input.turn_detection", "type").GetString().Should().Be(
            "server_vad", "server-side voice activity detection is what the options ask for");

        var tools = Member(session, "session", "tools").EnumerateArray().ToArray();
        tools.Should().ContainSingle("one tool is registered");
        Member(tools[0], "session.tools[0]", "type").GetString().Should().Be("function");
        Member(tools[0], "session.tools[0]", "name").GetString().Should().Be(tool.Name);
        Member(tools[0], "session.tools[0]", "description").GetString().Should().Be(tool.Description);
        ShouldBeJson(Member(tools[0], "session.tools[0]", "parameters"), tool.ParametersSchema, "session.tools[0].parameters");

        MemberNames(session).Should().NotContain(
            RetiredTopLevelMembers,
            "the endpoint refuses the retired top-level members as unknown parameters");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldSendANullTurnDetection_WhenVadIsDisabled()
    {
        // Arrange
        await using var fakeOpenAi = new RealtimeFakeServer();
        var options = StartingOptions();
        options.VadMode = VadMode.Disabled;

        // Act
        await OpenOneSessionAsync(fakeOpenAi, options);

        // Assert
        using var update = SessionUpdateOf(fakeOpenAi);
        var session = Member(update.RootElement, "", "session");
        var input = Member(Member(session, "session", "audio"), "session.audio", "input");

        Member(input, "session.audio.input", "turn_detection").ValueKind.Should().Be(
            JsonValueKind.Null,
            "an omitted turn detection is the vendor's default, server_vad, so only an explicit null disables it");
        MemberNames(session).Should().NotContain(
            "turn_detection", "the endpoint refuses a top-level turn_detection as an unknown parameter");
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldRequestGptRealtime_WhenTheModelIsNotSet()
    {
        // Arrange: the options a caller writes when it takes the default model, so Model is never set.
        await using var fakeOpenAi = new RealtimeFakeServer();
        var options = new OpenAiRealtimeOptions
        {
            ApiKey = ApiKey,
            InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
        };

        // Act
        await OpenOneSessionAsync(fakeOpenAi, options);

        // Assert
        var target = fakeOpenAi.UpgradeRequestUri;
        target.Should().NotBeNull("the fake captures the upgrade's target once it accepts a session");
        QueryValues(target!, "model").Should().ContainSingle(
                "the model travels once, in the upgrade's query, and the upgrade asked for {0}", target)
            .Which.Should().Be(
                "gpt-realtime",
                "the default is the generally available alias the vendor's model listing carries, and the endpoint closes a session on the old preview id with 4004 model_not_found; the upgrade asked for {0}",
                target);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Options as a test starts from: the fake's key and model, the defaults for the rest.</summary>
    private static OpenAiRealtimeOptions StartingOptions() => new()
    {
        ApiKey = ApiKey,
        Model = "gpt-realtime",
        InputFormat = Audio.AudioFormat.Slin16Mono8kHz,
    };

    /// <summary>
    /// Runs one session against <paramref name="fakeOpenAi"/> until the fake has its
    /// <c>session.update</c> and the bridge's loops are running, then ends it by cancelling its token.
    /// The fake keeps what it captured for the test to read.
    /// </summary>
    private static async Task OpenOneSessionAsync(
        RealtimeFakeServer fakeOpenAi,
        OpenAiRealtimeOptions options,
        params IRealtimeFunctionHandler[] handlers)
    {
        fakeOpenAi.EventsToSend.Add(LoopsRunningMarkerEvent);
        fakeOpenAi.Start();

        var (session, audioServer, client) = await CreateAudioSessionAsync();
        try
        {
            await using var bridge = new OpenAiRealtimeBridge(
                Options.Create(options),
                new RealtimeFunctionRegistry(handlers),
                NullLogger<OpenAiRealtimeBridge>.Instance)
            {
                BaseUri = new Uri($"ws://127.0.0.1:{fakeOpenAi.Port}/"),
            };

            using var loopsRunning = new RealtimeEventCollector(
                bridge.Events, e => e.OfType<RealtimeSpeechStartedEvent>().Any());
            using var cts = new CancellationTokenSource();

            var sessionTask = bridge.HandleSessionAsync(session, cts.Token).AsTask();
            await Task.WhenAll(fakeOpenAi.SessionUpdateReceived, loopsRunning.Satisfied).WaitAsync(SignalTimeout);
            await cts.CancelAsync();
            await sessionTask.WaitAsync(SignalTimeout);
        }
        finally
        {
            await client.SendHangupAsync();
            await client.DisposeAsync();
            await audioServer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<(AudioSocketSession session, AudioSocketServer audioServer, AudioSocketClient client)>
        CreateAudioSessionAsync()
    {
        var audioServer = new AudioSocketServer(
            new AudioSocketOptions { Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        var tcs = new TaskCompletionSource<AudioSocketSession>();
        audioServer.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await audioServer.StartAsync(CancellationToken.None);

        var client = new AudioSocketClient("127.0.0.1", audioServer.BoundPort, Guid.NewGuid());
        await client.ConnectAsync(CancellationToken.None);

        var session = await tcs.Task.WaitAsync(SignalTimeout);
        return (session, audioServer, client);
    }

    /// <summary>The one <c>session.update</c> the fake captured, parsed.</summary>
    private static JsonDocument SessionUpdateOf(RealtimeFakeServer fakeOpenAi)
    {
        var frame = fakeOpenAi.ReceivedMessages
            .Should().ContainSingle(m => m.Contains("\"type\":\"session.update\"", StringComparison.Ordinal))
            .Subject;
        return JsonDocument.Parse(frame);
    }

    /// <summary>
    /// The member <paramref name="name"/> of the object at <paramref name="path"/>. When it is missing,
    /// the failure lists the members the object does carry, which is what a wrong shape needs to show.
    /// </summary>
    private static JsonElement Member(JsonElement parent, string path, string name)
    {
        var at = path.Length == 0 ? name : $"{path}.{name}";
        parent.ValueKind.Should().Be(JsonValueKind.Object, "{0} is read from an object", at);
        var members = MemberNames(parent);
        members.Should().Contain(name, "the generally available shape carries {0}", at);
        return parent.GetProperty(name);
    }

    private static string[] MemberNames(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>
    /// Every value of the query parameter <paramref name="name"/> in <paramref name="requestTarget"/>
    /// (a path and query, as the fake captures it), percent-decoded, in the order they were sent.
    /// </summary>
    private static string[] QueryValues(string requestTarget, string name)
    {
        var queryStart = requestTarget.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return [];
        }

        return requestTarget[(queryStart + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
            .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty)
            .ToArray();
    }

    /// <summary>Asserts that <paramref name="actual"/> is the JSON value <paramref name="expected"/>, member order aside.</summary>
    private static void ShouldBeJson(JsonElement actual, string expected, string at)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        JsonElement.DeepEquals(actual, expectedDocument.RootElement).Should().BeTrue(
            "{0} should be {1}, and it is {2}", at, expected, actual.GetRawText());
    }

    /// <summary>A tool with a nested schema, so a schema the bridge rewrote rather than inserted as-is shows.</summary>
    private sealed class LookUpOrderFunction : IRealtimeFunctionHandler
    {
        public string Name => "look_up_order";
        public string Description => "Looks up an order by its number";
        public string ParametersSchema =>
            """{"type":"object","properties":{"order":{"type":"string","description":"The order number"},"include":{"type":"array","items":{"type":"string"}}},"required":["order"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("""{"status":"shipped"}""");
    }
}
