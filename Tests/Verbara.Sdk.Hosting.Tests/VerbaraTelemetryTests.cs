using Verbara.Sdk.Hosting;
using FluentAssertions;

namespace Verbara.Sdk.Hosting.Tests;

public sealed class VerbaraTelemetryTests
{
    [Fact]
    public void ActivitySourceNames_ShouldContainAllPackages()
    {
        VerbaraTelemetry.ActivitySourceNames.Should().HaveCount(9);
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Ami");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Ari");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Agi");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Live");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Sessions");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.Push");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.VoiceAi");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.VoiceAi.AudioSocket");
        VerbaraTelemetry.ActivitySourceNames.Should().Contain("Verbara.Sdk.VoiceAi.OpenAiRealtime");
    }

    [Fact]
    public void MeterNames_ShouldContainAllPackages()
    {
        VerbaraTelemetry.MeterNames.Should().HaveCount(15);
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Ami");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Ari");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Ari.Audio");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Agi");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Live");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Sessions");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Push");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Push.Webhooks");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Push.Nats");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.Resilience");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.VoiceAi");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.VoiceAi.Stt");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.VoiceAi.Tts");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.VoiceAi.AudioSocket");
        VerbaraTelemetry.MeterNames.Should().Contain("Verbara.Sdk.VoiceAi.OpenAiRealtime");
    }
}
