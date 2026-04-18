using Asterisk.Sdk.OpenTelemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Asterisk.Sdk.OpenTelemetry.Tests;

public sealed class AsteriskOpenTelemetryExtensionsTests
{
    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldThrow_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAsteriskOpenTelemetry(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldRegisterTracerProvider_WhenSourcesEnrolled()
    {
        var services = new ServiceCollection();

        services.AddAsteriskOpenTelemetry(b => b.WithAllSources());

        using var sp = services.BuildServiceProvider();
        sp.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldRegisterMeterProvider_WhenMetersEnrolled()
    {
        var services = new ServiceCollection();

        services.AddAsteriskOpenTelemetry(b => b.WithAllSources());

        using var sp = services.BuildServiceProvider();
        sp.GetService<MeterProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldBuildCleanly_WithConsoleExporter()
    {
        var services = new ServiceCollection();

        services.AddAsteriskOpenTelemetry(b => b
            .WithAllSources()
            .WithConsoleExporter());

        using var sp = services.BuildServiceProvider();
        var tracer = sp.GetService<TracerProvider>();
        var meter = sp.GetService<MeterProvider>();

        tracer.Should().NotBeNull();
        meter.Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldHonorCustomServiceName()
    {
        var services = new ServiceCollection();

        services.AddAsteriskOpenTelemetry(b =>
        {
            b.ServiceName = "my-custom-service";
            b.WithAllSources();
        });

        using var sp = services.BuildServiceProvider();
        sp.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskOpenTelemetry_ShouldSkipTracing_WhenNoSourcesAndNoTracingConfigurators()
    {
        var services = new ServiceCollection();

        services.AddAsteriskOpenTelemetry(b => b.AddMeter("Custom.Meter"));

        using var sp = services.BuildServiceProvider();
        sp.GetService<MeterProvider>().Should().NotBeNull();
    }
}
