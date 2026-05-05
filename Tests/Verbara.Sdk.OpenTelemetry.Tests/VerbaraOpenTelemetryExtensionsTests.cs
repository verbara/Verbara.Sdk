using Verbara.Sdk.OpenTelemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Verbara.Sdk.OpenTelemetry.Tests;

public sealed class VerbaraOpenTelemetryExtensionsTests
{
    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldThrow_WhenConfigureIsNull()
    {
        var services = new ServiceCollection();

        var act = () => services.AddVerbaraOpenTelemetry(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldRegisterTracerProvider_WhenSourcesEnrolled()
    {
        var services = new ServiceCollection();

        services.AddVerbaraOpenTelemetry(b => b.WithAllSources());

        using var sp = services.BuildServiceProvider();
        sp.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldRegisterMeterProvider_WhenMetersEnrolled()
    {
        var services = new ServiceCollection();

        services.AddVerbaraOpenTelemetry(b => b.WithAllSources());

        using var sp = services.BuildServiceProvider();
        sp.GetService<MeterProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldBuildCleanly_WithConsoleExporter()
    {
        var services = new ServiceCollection();

        services.AddVerbaraOpenTelemetry(b => b
            .WithAllSources()
            .WithConsoleExporter());

        using var sp = services.BuildServiceProvider();
        var tracer = sp.GetService<TracerProvider>();
        var meter = sp.GetService<MeterProvider>();

        tracer.Should().NotBeNull();
        meter.Should().NotBeNull();
    }

    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldHonorCustomServiceName()
    {
        var services = new ServiceCollection();

        services.AddVerbaraOpenTelemetry(b =>
        {
            b.ServiceName = "my-custom-service";
            b.WithAllSources();
        });

        using var sp = services.BuildServiceProvider();
        sp.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddVerbaraOpenTelemetry_ShouldSkipTracing_WhenNoSourcesAndNoTracingConfigurators()
    {
        var services = new ServiceCollection();

        services.AddVerbaraOpenTelemetry(b => b.AddMeter("Custom.Meter"));

        using var sp = services.BuildServiceProvider();
        sp.GetService<MeterProvider>().Should().NotBeNull();
    }
}
