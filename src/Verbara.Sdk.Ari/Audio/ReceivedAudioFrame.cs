namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// One audio frame waiting in a session's inbound queue: its payload, and the
/// <see cref="System.Diagnostics.Stopwatch"/> timestamp taken when it was received, from which the read
/// that hands it to the consumer records <c>audio.frame.latency</c>.
/// </summary>
/// <param name="Payload">The frame's audio, header excluded.</param>
/// <param name="ReceivedTimestamp">The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value at receipt.</param>
internal readonly record struct ReceivedAudioFrame(ReadOnlyMemory<byte> Payload, long ReceivedTimestamp);
