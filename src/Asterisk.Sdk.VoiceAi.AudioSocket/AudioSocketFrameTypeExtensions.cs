namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>Extension methods for <see cref="AudioSocketFrameType"/>.</summary>
public static class AudioSocketFrameTypeExtensions
{
    /// <summary>Gets the sample rate in Hz for an audio frame type.</summary>
    public static int GetSampleRate(this AudioSocketFrameType type) => type switch
    {
        AudioSocketFrameType.Audio => 8000,
        AudioSocketFrameType.AudioSlin12 => 12000,
        AudioSocketFrameType.AudioSlin16 => 16000,
        AudioSocketFrameType.AudioSlin24 => 24000,
        AudioSocketFrameType.AudioSlin32 => 32000,
        AudioSocketFrameType.AudioSlin44 => 44100,
        AudioSocketFrameType.AudioSlin48 => 48000,
        AudioSocketFrameType.AudioSlin96 => 96000,
        AudioSocketFrameType.AudioSlin192 => 192000,
        _ => 8000,
    };

    /// <summary>Returns true if the frame type carries audio data.</summary>
    public static bool IsAudio(this AudioSocketFrameType type) =>
        type is AudioSocketFrameType.Audio
            or AudioSocketFrameType.AudioSlin12
            or AudioSocketFrameType.AudioSlin16
            or AudioSocketFrameType.AudioSlin24
            or AudioSocketFrameType.AudioSlin32
            or AudioSocketFrameType.AudioSlin44
            or AudioSocketFrameType.AudioSlin48
            or AudioSocketFrameType.AudioSlin96
            or AudioSocketFrameType.AudioSlin192;
}
