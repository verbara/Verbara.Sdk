using Verbara.Sdk.Hosting;
using Verbara.Sdk.OpenTelemetry;
using FluentAssertions;

namespace Verbara.Sdk.OpenTelemetry.Tests;

public sealed class VerbaraOpenTelemetryBuilderTests
{
    [Fact]
    public void WithAllSources_ShouldEnrollEveryActivitySource_FromVerbaraTelemetry()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithAllSources();

        builder.ActivitySources.Should().BeEquivalentTo(VerbaraTelemetry.ActivitySourceNames);
    }

    [Fact]
    public void WithAllSources_ShouldEnrollEveryMeter_FromVerbaraTelemetry()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithAllSources();

        builder.Meters.Should().BeEquivalentTo(VerbaraTelemetry.MeterNames);
    }

    [Fact]
    public void WithAllSources_ShouldBeIdempotent_WhenCalledTwice()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithAllSources();
        builder.WithAllSources();

        builder.ActivitySources.Distinct().Count().Should().Be(builder.ActivitySources.Count);
        builder.Meters.Distinct().Count().Should().Be(builder.Meters.Count);
    }

    [Fact]
    public void AddActivitySource_ShouldAddName_WhenNotAlreadyPresent()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.AddActivitySource("Custom.Source");
        builder.AddActivitySource("Custom.Source");

        builder.ActivitySources.Should().ContainSingle(n => n == "Custom.Source");
    }

    [Fact]
    public void AddMeter_ShouldAddName_WhenNotAlreadyPresent()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.AddMeter("Custom.Meter");
        builder.AddMeter("Custom.Meter");

        builder.Meters.Should().ContainSingle(n => n == "Custom.Meter");
    }

    [Fact]
    public void ServiceName_ShouldDefaultTo_AsteriskSdk()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.ServiceName.Should().Be("asterisk-sdk");
    }

    [Fact]
    public void ServiceName_Set_ShouldThrow_WhenValueIsWhitespace()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        var act = () => builder.ServiceName = "   ";

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithConsoleExporter_ShouldQueueOne_TracingAndOne_MetricsConfigurator()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithConsoleExporter();

        builder.TracingConfigurators.Should().HaveCount(1);
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void WithPrometheusExporter_ShouldQueueOnly_MetricsConfigurator()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithPrometheusExporter();

        builder.TracingConfigurators.Should().BeEmpty();
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void WithOtlpExporter_ShouldQueueBoth_TracingAndMetricsConfigurators()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        builder.WithOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"));

        builder.TracingConfigurators.Should().HaveCount(1);
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void ConfigureTracing_ShouldThrow_WhenConfigureIsNull()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        var act = () => builder.ConfigureTracing(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureMetrics_ShouldThrow_WhenConfigureIsNull()
    {
        var builder = new VerbaraOpenTelemetryBuilder();

        var act = () => builder.ConfigureMetrics(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
