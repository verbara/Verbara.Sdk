using Asterisk.Sdk.Hosting;
using FluentAssertions;

namespace Asterisk.Sdk.Hosting.Tests;

public sealed class AsteriskTelemetryTests
{
    [Fact]
    public void ActivitySourceNames_ShouldContainAllPackages()
    {
        AsteriskTelemetry.ActivitySourceNames.Should().HaveCount(9);
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Ami");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Ari");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Agi");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Live");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Sessions");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Push");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.VoiceAi");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.VoiceAi.AudioSocket");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.VoiceAi.OpenAiRealtime");
    }

    [Fact]
    public void MeterNames_ShouldContainAllPackages()
    {
        AsteriskTelemetry.MeterNames.Should().HaveCount(12);
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ami");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ari");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ari.Audio");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Agi");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Live");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Sessions");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Push");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.VoiceAi");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.VoiceAi.Stt");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.VoiceAi.Tts");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.VoiceAi.AudioSocket");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.VoiceAi.OpenAiRealtime");
    }
}
