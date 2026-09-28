using System.Runtime.CompilerServices;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Stt.AssemblyAi;
using Verbara.Sdk.VoiceAi.Stt.Cartesia;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using Verbara.Sdk.VoiceAi.Stt.Tests.AssemblyAi;
using Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;
using Microsoft.Extensions.Options;
using Xunit;
using CartesiaSttFake = Verbara.Sdk.VoiceAi.Stt.Tests.Cartesia.CartesiaFakeServer;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// An <see cref="EndOfInputPeer"/> per WebSocket recognizer: the frame each client ends its input with,
/// what the vendor says first, and what it answers with, all from the recorded frames the per-vendor
/// fakes already serve.
/// </summary>
/// <remarks>
/// Each profile carries an interim transcript before the end of input (one item), the same interim
/// transcript as its <see cref="EndOfInputPeer.Progress"/> round (one item per round), and a final
/// transcript plus the vendor's own session ending as its <see cref="EndOfInputPeer.Answer"/> (one
/// item). The matchers are the ones the probes that first measured these waits used.
/// </remarks>
internal static class RecognizerEndOfInputPeers
{
    /// <summary>The four recognizers that end their input in band and then wait for the vendor.</summary>
    public static TheoryData<string> Clients => ["Deepgram", "AssemblyAI", "Cartesia", "Speechmatics"];

    /// <summary>The peer that speaks <paramref name="client"/>'s protocol up to its end of input.</summary>
    public static EndOfInputPeer Create(string client, EndOfInputPeerMode mode) => client switch
    {
        "Deepgram" => new EndOfInputPeer(mode)
        {
            Preamble = [PeerFrame.Text(DeepgramFakeServer.BuildResultJson("hello", 0.9f, isFinal: false))],
            IsEndOfInput = static t => t.Contains("CloseStream", StringComparison.Ordinal),
            Progress = [PeerFrame.Text(DeepgramFakeServer.BuildResultJson("hello", 0.9f, isFinal: false))],
            Answer =
            [
                PeerFrame.Text(DeepgramFakeServer.BuildResultJson("hello world", 0.95f, isFinal: true)),
                PeerFrame.Text(DeepgramFakeServer.ReadFrame(DeepgramFakeServer.MetadataFrame)),
            ],
        },
        "AssemblyAI" => new EndOfInputPeer(mode)
        {
            Preamble =
            [
                PeerFrame.Text(AssemblyAiFakeServer.BuildBeginJson()),
                PeerFrame.Text(AssemblyAiFakeServer.BuildTurnJson("hello", endOfTurn: false)),
            ],
            IsEndOfInput = static t => t.Contains("Terminate", StringComparison.Ordinal),
            Progress = [PeerFrame.Text(AssemblyAiFakeServer.BuildTurnJson("hello", endOfTurn: false))],
            Answer =
            [
                PeerFrame.Text(AssemblyAiFakeServer.BuildTurnJson("hello world", endOfTurn: true)),
                PeerFrame.Text(AssemblyAiFakeServer.BuildTerminationJson()),
            ],
        },
        "Cartesia" => new EndOfInputPeer(mode)
        {
            Preamble = [PeerFrame.Text(CartesiaSttFake.BuildTranscriptJson("hello", 0.9f, isFinal: false))],
            IsEndOfInput = static t => t == "done",
            Progress = [PeerFrame.Text(CartesiaSttFake.BuildTranscriptJson("hello", 0.9f, isFinal: false))],
            Answer =
            [
                PeerFrame.Text(CartesiaSttFake.BuildTranscriptJson("hello world", 0.95f, isFinal: true)),
                PeerFrame.Text(CartesiaSttFake.ReadFrame(CartesiaSttFake.DoneFrame)),
            ],
        },
        "Speechmatics" => new EndOfInputPeer(mode)
        {
            // Speechmatics speaks only after the client's StartRecognition.
            Replies = static t => t.Contains("StartRecognition", StringComparison.Ordinal)
                ? [
                    PeerFrame.Text(SpeechmaticsFakeServer.BuildRecognitionStartedJson()),
                    PeerFrame.Text(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hello", 0.9f)),
                  ]
                : [],
            IsEndOfInput = static t => t.Contains("EndOfStream", StringComparison.Ordinal),
            Progress = [PeerFrame.Text(SpeechmaticsFakeServer.BuildPartialTranscriptJson("hello", 0.9f))],
            Answer =
            [
                PeerFrame.Text(SpeechmaticsFakeServer.BuildFinalTranscriptJson("hello world", 0.95f)),
                PeerFrame.Text(SpeechmaticsFakeServer.BuildEndOfTranscriptJson()),
            ],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    /// <summary>
    /// The recognizer <paramref name="client"/> names, pointed at a peer on <paramref name="port"/>,
    /// with its bounds on <paramref name="clock"/> (<see cref="TimeProvider.System"/> when none is given).
    /// </summary>
    public static SpeechRecognizer CreateRecognizer(string client, int port, TimeProvider? clock = null) => client switch
    {
        "Deepgram" => new DeepgramSpeechRecognizer(Options.Create(new DeepgramOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v1/listen",
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "AssemblyAI" => new AssemblyAiSpeechRecognizer(Options.Create(new AssemblyAiOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v3/ws",
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "Cartesia" => new CartesiaSpeechRecognizer(Options.Create(new CartesiaOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/stt/websocket",
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "Speechmatics" => new SpeechmaticsSpeechRecognizer(Options.Create(new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v2",
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    /// <summary>The caller's audio: <paramref name="count"/> 20 ms frames of 8 kHz slin16, then the end of input.</summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> Frames(
        int count, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new byte[320];
            await Task.Yield();
        }
    }
}
