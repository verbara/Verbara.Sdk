using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Verbara.Sdk.OpenTelemetry;

/// <summary>
/// OpenTelemetry wiring extensions for Verbara.Sdk consumers.
/// </summary>
public static class VerbaraOpenTelemetryExtensions
{
    /// <summary>
    /// Configure OpenTelemetry tracing and metrics with the Asterisk SDK's ActivitySources and
    /// Meters pre-enrolled. Callers opt into exporters via the fluent builder:
    /// <code>
    /// services.AddVerbaraOpenTelemetry(b =&gt; b
    ///     .WithAllSources()
    ///     .WithPrometheusExporter()
    ///     .WithOtlpExporter(o =&gt; o.Endpoint = new Uri("http://tempo:4317")));
    /// </code>
    /// Internally delegates to the standard <c>services.AddOpenTelemetry().WithTracing(...).WithMetrics(...)</c>
    /// pipeline so consumers who also depend on the raw OpenTelemetry API can keep composing.
    /// </summary>
    public static IServiceCollection AddVerbaraOpenTelemetry(
        this IServiceCollection services,
        Action<VerbaraOpenTelemetryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new VerbaraOpenTelemetryBuilder();
        configure(builder);

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.ServiceName));

        if (builder.ActivitySources.Count > 0 || builder.TracingConfigurators.Count > 0)
        {
            otel.WithTracing(tracing =>
            {
                foreach (var source in builder.ActivitySources)
                    tracing.AddSource(source);
                foreach (var configurator in builder.TracingConfigurators)
                    configurator(tracing);
            });
        }

        if (builder.Meters.Count > 0 || builder.MetricsConfigurators.Count > 0)
        {
            otel.WithMetrics(metrics =>
            {
                foreach (var meter in builder.Meters)
                    metrics.AddMeter(meter);
                foreach (var configurator in builder.MetricsConfigurators)
                    configurator(metrics);
            });
        }

        return services;
    }
}
