using Asterisk.Sdk.Hosting;
using FluentAssertions;

namespace Asterisk.Sdk.Hosting.Tests;

public sealed class AsteriskTelemetryTests
{
    [Fact]
    public void ActivitySourceNames_ShouldContainAllPackages()
    {
        AsteriskTelemetry.ActivitySourceNames.Should().HaveCount(6);
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Ami");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Ari");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Agi");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Live");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Sessions");
        AsteriskTelemetry.ActivitySourceNames.Should().Contain("Asterisk.Sdk.Push");
    }

    [Fact]
    public void MeterNames_ShouldContainAllPackages()
    {
        AsteriskTelemetry.MeterNames.Should().HaveCount(7);
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ami");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ari");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Ari.Audio");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Agi");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Live");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Sessions");
        AsteriskTelemetry.MeterNames.Should().Contain("Asterisk.Sdk.Push");
    }
}
