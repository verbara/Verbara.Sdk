# Asterisk.Sdk.Audio

Audio processing library for Asterisk.Sdk — resampling, format conversion, gain, and silence detection.

## Installation

```bash
dotnet add package Asterisk.Sdk.Audio
```

## Quick Start

```csharp
// Resample from 8 kHz (Asterisk native) to 16 kHz (AI model input)
var resampler = ResamplerFactory.Create(8000, 16000);
var input = AudioFormat.Slin16Mono8kHz;
var output = AudioFormat.Slin16Mono16kHz;

var maxBytes = resampler.MaxOutputBytes(inputBytes.Length);
var outBuf = new byte[maxBytes];
var written = resampler.Process(inputBytes, outBuf);

// Convert PCM16 to normalized float32 for AI model inference
var shorts = MemoryMarshal.Cast<byte, short>(inputBytes.AsSpan());
var floats = new float[shorts.Length];
AudioProcessor.ConvertToFloat32(shorts, floats);

// Silence detection (default -40 dBFS threshold)
if (AudioProcessor.IsSilence(shorts))
    Console.WriteLine("Silence detected");
```

## Features

- `ResamplerFactory` — polyphase resampler for 12 telephony rate pairs (8/16/24/48 kHz)
- `AudioProcessor` — PCM16↔float32 conversion, gain (dB), RMS energy, silence detection
- `AudioFormat` — immutable value type with predefined telephony formats (`Slin16Mono8kHz`, `Float32Mono16kHz`, etc.)
- `IAudioTransform` — chainable processing interface for custom pipeline stages
- Zero-alloc `Span<T>`-based API throughout; Native AOT compatible

## Documentation

See the [main README](../../README.md) for full documentation.
