using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketFrameTypeTests
{
    // ── Byte values ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioSocketFrameType.Uuid, 0x00)]
    [InlineData(AudioSocketFrameType.Audio, 0x01)]
    [InlineData(AudioSocketFrameType.Silence, 0x02)]
    [InlineData(AudioSocketFrameType.Error, 0x04)]
    [InlineData(AudioSocketFrameType.AudioSlin12, 0x11)]
    [InlineData(AudioSocketFrameType.AudioSlin16, 0x12)]
    [InlineData(AudioSocketFrameType.AudioSlin24, 0x13)]
    [InlineData(AudioSocketFrameType.AudioSlin32, 0x14)]
    [InlineData(AudioSocketFrameType.AudioSlin44, 0x15)]
    [InlineData(AudioSocketFrameType.AudioSlin48, 0x16)]
    [InlineData(AudioSocketFrameType.AudioSlin96, 0x17)]
    [InlineData(AudioSocketFrameType.AudioSlin192, 0x18)]
    [InlineData(AudioSocketFrameType.Hangup, 0xFF)]
    public void FrameType_ShouldHaveCorrectByteValue(AudioSocketFrameType type, byte expectedValue)
    {
        ((byte)type).Should().Be(expectedValue);
    }

    // ── GetSampleRate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioSocketFrameType.Audio, 8000)]
    [InlineData(AudioSocketFrameType.AudioSlin12, 12000)]
    [InlineData(AudioSocketFrameType.AudioSlin16, 16000)]
    [InlineData(AudioSocketFrameType.AudioSlin24, 24000)]
    [InlineData(AudioSocketFrameType.AudioSlin32, 32000)]
    [InlineData(AudioSocketFrameType.AudioSlin44, 44100)]
    [InlineData(AudioSocketFrameType.AudioSlin48, 48000)]
    [InlineData(AudioSocketFrameType.AudioSlin96, 96000)]
    [InlineData(AudioSocketFrameType.AudioSlin192, 192000)]
    public void GetSampleRate_ShouldReturnCorrectHz(AudioSocketFrameType type, int expectedHz)
    {
        type.GetSampleRate().Should().Be(expectedHz);
    }

    [Theory]
    [InlineData(AudioSocketFrameType.Uuid)]
    [InlineData(AudioSocketFrameType.Silence)]
    [InlineData(AudioSocketFrameType.Error)]
    [InlineData(AudioSocketFrameType.Hangup)]
    public void GetSampleRate_ShouldReturn8000_ForNonAudioTypes(AudioSocketFrameType type)
    {
        type.GetSampleRate().Should().Be(8000);
    }

    // ── IsAudio ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioSocketFrameType.Audio)]
    [InlineData(AudioSocketFrameType.AudioSlin12)]
    [InlineData(AudioSocketFrameType.AudioSlin16)]
    [InlineData(AudioSocketFrameType.AudioSlin24)]
    [InlineData(AudioSocketFrameType.AudioSlin32)]
    [InlineData(AudioSocketFrameType.AudioSlin44)]
    [InlineData(AudioSocketFrameType.AudioSlin48)]
    [InlineData(AudioSocketFrameType.AudioSlin96)]
    [InlineData(AudioSocketFrameType.AudioSlin192)]
    public void IsAudio_ShouldReturnTrue_ForAllAudioFrameTypes(AudioSocketFrameType type)
    {
        type.IsAudio().Should().BeTrue();
    }

    [Theory]
    [InlineData(AudioSocketFrameType.Uuid)]
    [InlineData(AudioSocketFrameType.Silence)]
    [InlineData(AudioSocketFrameType.Error)]
    [InlineData(AudioSocketFrameType.Hangup)]
    public void IsAudio_ShouldReturnFalse_ForNonAudioFrameTypes(AudioSocketFrameType type)
    {
        type.IsAudio().Should().BeFalse();
    }
}
