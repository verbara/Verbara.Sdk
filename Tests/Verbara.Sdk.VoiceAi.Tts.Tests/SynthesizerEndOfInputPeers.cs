using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.Cartesia;
using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using Verbara.Sdk.VoiceAi.Tts.Lmnt;
using Verbara.Sdk.VoiceAi.Tts.Tests.Cartesia;
using Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;
using Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;
using Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// An <see cref="EndOfInputPeer"/> per WebSocket synthesizer: the frame each client ends its input
/// with, and the vendor's audio and terminal frame, from the recorded frames the per-vendor fakes
/// already serve.
/// </summary>
/// <remarks>
/// Each profile answers the end of input with one audio frame (one item), sends the same audio frame
/// as its <see cref="EndOfInputPeer.Progress"/> round (one item per round), and ends with the vendor's
/// terminal frame as its <see cref="EndOfInputPeer.Answer"/> (no item). The matchers are the ones the
/// probes that first measured these waits used. Deepgram's matches its <c>Close</c>, which the client
/// sends after its <c>Flush</c>.
/// </remarks>
internal static class SynthesizerEndOfInputPeers
{
    /// <summary>The four synthesizers that end their input in band and then wait for the vendor.</summary>
    public static TheoryData<string> Clients => ["Cartesia", "Deepgram", "ElevenLabs", "LMNT"];

    /// <summary>The peer that speaks <paramref name="client"/>'s protocol up to its end of input.</summary>
    public static EndOfInputPeer Create(string client, EndOfInputPeerMode mode) => client switch
    {
        "Cartesia" => new EndOfInputPeer(mode)
        {
            IsEndOfInput = static t => t.Contains("\"transcript\"", StringComparison.Ordinal),
            OnEndOfInput = [PeerFrame.Text(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.ChunkFrame))],
            Progress = [PeerFrame.Text(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.ChunkFrame))],
            Answer = [PeerFrame.Text(CartesiaFakeServer.ReadFrame(CartesiaFakeServer.DoneFrame))],
        },
        "Deepgram" => new EndOfInputPeer(mode)
        {
            IsEndOfInput = static t => t.Contains("\"Close\"", StringComparison.Ordinal),
            OnEndOfInput = [PeerFrame.Binary(320)],
            Progress = [PeerFrame.Binary(320)],
            Answer = [PeerFrame.Text(DeepgramTtsFakeServer.ReadFrame(DeepgramTtsFakeServer.FlushedFrame))],
        },
        "ElevenLabs" => new EndOfInputPeer(mode)
        {
            IsEndOfInput = static t => t.Contains("\"text\":\"\"", StringComparison.Ordinal),
            OnEndOfInput = [PeerFrame.Text(ElevenLabsFakeServer.ReadFrame(ElevenLabsFakeServer.AudioOutputFrame))],
            Progress = [PeerFrame.Text(ElevenLabsFakeServer.ReadFrame(ElevenLabsFakeServer.AudioOutputFrame))],
            Answer = [PeerFrame.Text("""{"isFinal":true}""")],
        },
        "LMNT" => new EndOfInputPeer(mode)
        {
            IsEndOfInput = static t => t.Contains("\"eof\"", StringComparison.Ordinal),
            OnEndOfInput = [PeerFrame.Binary(320)],
            Progress = [PeerFrame.Binary(320)],
            Answer = [PeerFrame.Text(LmntWsFakeServer.ReadFrame(LmntWsFakeServer.FinishFrame))],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    /// <summary>
    /// The synthesizer <paramref name="client"/> names, pointed at a peer on <paramref name="port"/>,
    /// with its bounds on <paramref name="clock"/> (<see cref="TimeProvider.System"/> when none is given)
    /// and its <c>ConnectTimeoutSeconds</c> at <paramref name="connectTimeoutSeconds"/> (the option's own
    /// default when none is given).
    /// </summary>
    public static SpeechSynthesizer CreateSynthesizer(
        string client, int port, TimeProvider? clock = null, int? connectTimeoutSeconds = null) => client switch
    {
        "Cartesia" => new CartesiaSpeechSynthesizer(Options.Create(new CartesiaOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{port}/tts/websocket",
            ConnectTimeoutSeconds = connectTimeoutSeconds ?? new CartesiaOptions().ConnectTimeoutSeconds,
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "Deepgram" => new DeepgramSpeechSynthesizer(Options.Create(new DeepgramTtsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v1/speak",
            ConnectTimeoutSeconds = connectTimeoutSeconds ?? new DeepgramTtsOptions().ConnectTimeoutSeconds,
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "ElevenLabs" => new ElevenLabsSpeechSynthesizer(Options.Create(new ElevenLabsOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{port}/v1/text-to-speech",
            ConnectTimeoutSeconds = connectTimeoutSeconds ?? new ElevenLabsOptions().ConnectTimeoutSeconds,
        }))
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        "LMNT" => new LmntSpeechSynthesizer(Options.Create(new LmntTtsOptions
        {
            ApiKey = "test-key",
            Voice = LmntVoices.Leah,
            Transport = LmntTransport.WebSocket,
            ConnectTimeoutSeconds = connectTimeoutSeconds ?? new LmntTtsOptions().ConnectTimeoutSeconds,
        }), port)
        {
            TimeProvider = clock ?? TimeProvider.System,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };
}
