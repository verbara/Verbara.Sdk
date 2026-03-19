namespace Asterisk.Sdk.Audio;

/// <summary>Encoding format for audio samples.</summary>
public enum AudioEncoding
{
    /// <summary>Signed 16-bit linear PCM (little-endian).</summary>
    LinearPcm,

    /// <summary>32-bit IEEE 754 floating-point, normalized -1.0 to 1.0.</summary>
    IeeeFloat,
}
