namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>A single AudioSocket protocol frame.</summary>
public readonly record struct AudioSocketFrame(
    AudioSocketFrameType Type,
    ReadOnlyMemory<byte> Payload);
