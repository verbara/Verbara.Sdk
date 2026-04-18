using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.OpenTelemetry;
using FluentAssertions;

namespace Asterisk.Sdk.OpenTelemetry.Tests;

public sealed class AsteriskOpenTelemetryBuilderTests
{
    [Fact]
    public void WithAllSources_ShouldEnrollEveryActivitySource_FromAsteriskTelemetry()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithAllSources();

        builder.ActivitySources.Should().BeEquivalentTo(AsteriskTelemetry.ActivitySourceNames);
    }

    [Fact]
    public void WithAllSources_ShouldEnrollEveryMeter_FromAsteriskTelemetry()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithAllSources();

        builder.Meters.Should().BeEquivalentTo(AsteriskTelemetry.MeterNames);
    }

    [Fact]
    public void WithAllSources_ShouldBeIdempotent_WhenCalledTwice()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithAllSources();
        builder.WithAllSources();

        builder.ActivitySources.Distinct().Count().Should().Be(builder.ActivitySources.Count);
        builder.Meters.Distinct().Count().Should().Be(builder.Meters.Count);
    }

    [Fact]
    public void AddActivitySource_ShouldAddName_WhenNotAlreadyPresent()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.AddActivitySource("Custom.Source");
        builder.AddActivitySource("Custom.Source");

        builder.ActivitySources.Should().ContainSingle(n => n == "Custom.Source");
    }

    [Fact]
    public void AddMeter_ShouldAddName_WhenNotAlreadyPresent()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.AddMeter("Custom.Meter");
        builder.AddMeter("Custom.Meter");

        builder.Meters.Should().ContainSingle(n => n == "Custom.Meter");
    }

    [Fact]
    public void ServiceName_ShouldDefaultTo_AsteriskSdk()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.ServiceName.Should().Be("asterisk-sdk");
    }

    [Fact]
    public void ServiceName_Set_ShouldThrow_WhenValueIsWhitespace()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        var act = () => builder.ServiceName = "   ";

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithConsoleExporter_ShouldQueueOne_TracingAndOne_MetricsConfigurator()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithConsoleExporter();

        builder.TracingConfigurators.Should().HaveCount(1);
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void WithPrometheusExporter_ShouldQueueOnly_MetricsConfigurator()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithPrometheusExporter();

        builder.TracingConfigurators.Should().BeEmpty();
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void WithOtlpExporter_ShouldQueueBoth_TracingAndMetricsConfigurators()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        builder.WithOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"));

        builder.TracingConfigurators.Should().HaveCount(1);
        builder.MetricsConfigurators.Should().HaveCount(1);
    }

    [Fact]
    public void ConfigureTracing_ShouldThrow_WhenConfigureIsNull()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        var act = () => builder.ConfigureTracing(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureMetrics_ShouldThrow_WhenConfigureIsNull()
    {
        var builder = new AsteriskOpenTelemetryBuilder();

        var act = () => builder.ConfigureMetrics(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
