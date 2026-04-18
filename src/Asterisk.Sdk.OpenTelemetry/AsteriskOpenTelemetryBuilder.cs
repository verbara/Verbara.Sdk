using Asterisk.Sdk.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Asterisk.Sdk.OpenTelemetry;

/// <summary>
/// Fluent builder passed to <see cref="AsteriskOpenTelemetryExtensions.AddAsteriskOpenTelemetry"/>.
/// Collects which <see cref="System.Diagnostics.ActivitySource"/>s and
/// <see cref="System.Diagnostics.Metrics.Meter"/>s to enroll, plus callbacks that configure
/// the underlying <see cref="TracerProviderBuilder"/> / <see cref="MeterProviderBuilder"/>
/// (exporters, samplers, resource detection, etc.).
/// </summary>
public sealed class AsteriskOpenTelemetryBuilder
{
    private readonly List<string> _activitySources = [];
    private readonly List<string> _meters = [];
    private readonly List<Action<TracerProviderBuilder>> _tracingConfigurators = [];
    private readonly List<Action<MeterProviderBuilder>> _metricsConfigurators = [];
    private string _serviceName = "asterisk-sdk";

    /// <summary>The service.name that decorates exported telemetry. Defaults to "asterisk-sdk".</summary>
    public string ServiceName
    {
        get => _serviceName;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _serviceName = value;
        }
    }

    internal IReadOnlyList<string> ActivitySources => _activitySources;
    internal IReadOnlyList<string> Meters => _meters;
    internal IReadOnlyList<Action<TracerProviderBuilder>> TracingConfigurators => _tracingConfigurators;
    internal IReadOnlyList<Action<MeterProviderBuilder>> MetricsConfigurators => _metricsConfigurators;

    /// <summary>
    /// Enroll every Asterisk SDK <see cref="AsteriskTelemetry.ActivitySourceNames"/> and
    /// <see cref="AsteriskTelemetry.MeterNames"/> for export. Safe to call multiple times —
    /// duplicate names are deduplicated before enrollment.
    /// </summary>
    public AsteriskOpenTelemetryBuilder WithAllSources()
    {
        foreach (var name in AsteriskTelemetry.ActivitySourceNames)
            if (!_activitySources.Contains(name, StringComparer.Ordinal))
                _activitySources.Add(name);

        foreach (var name in AsteriskTelemetry.MeterNames)
            if (!_meters.Contains(name, StringComparer.Ordinal))
                _meters.Add(name);

        return this;
    }

    /// <summary>Enroll an additional <see cref="System.Diagnostics.ActivitySource"/> name for export.</summary>
    public AsteriskOpenTelemetryBuilder AddActivitySource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_activitySources.Contains(name, StringComparer.Ordinal))
            _activitySources.Add(name);
        return this;
    }

    /// <summary>Enroll an additional <see cref="System.Diagnostics.Metrics.Meter"/> name for export.</summary>
    public AsteriskOpenTelemetryBuilder AddMeter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_meters.Contains(name, StringComparer.Ordinal))
            _meters.Add(name);
        return this;
    }

    /// <summary>
    /// Write every trace span and metric sample to the console. Useful for local development
    /// and smoke testing. Do not enable in production.
    /// </summary>
    public AsteriskOpenTelemetryBuilder WithConsoleExporter()
    {
        _tracingConfigurators.Add(static t => t.AddConsoleExporter());
        _metricsConfigurators.Add(static m => m.AddConsoleExporter());
        return this;
    }

    /// <summary>
    /// Send traces and metrics to an OTLP endpoint (Tempo, Jaeger, OpenTelemetry Collector, etc.).
    /// The default endpoint is <c>http://localhost:4317</c> (gRPC). Override via
    /// <paramref name="configure"/>.
    /// </summary>
    public AsteriskOpenTelemetryBuilder WithOtlpExporter(
        Action<global::OpenTelemetry.Exporter.OtlpExporterOptions>? configure = null)
    {
        _tracingConfigurators.Add(t => t.AddOtlpExporter(o => configure?.Invoke(o)));
        _metricsConfigurators.Add(m => m.AddOtlpExporter(o => configure?.Invoke(o)));
        return this;
    }

    /// <summary>
    /// Enable the Prometheus metrics exporter (ASP.NET Core integration). Consumers must wire
    /// <c>app.UseOpenTelemetryPrometheusScrapingEndpoint()</c> on the ASP.NET Core pipeline to
    /// expose the <c>/metrics</c> endpoint.
    /// </summary>
    public AsteriskOpenTelemetryBuilder WithPrometheusExporter()
    {
        _metricsConfigurators.Add(static m => m.AddPrometheusExporter());
        return this;
    }

    /// <summary>
    /// Apply a custom configuration delegate to the <see cref="TracerProviderBuilder"/>
    /// (e.g., to add samplers, processors, or additional exporters beyond the built-in helpers).
    /// </summary>
    public AsteriskOpenTelemetryBuilder ConfigureTracing(Action<TracerProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _tracingConfigurators.Add(configure);
        return this;
    }

    /// <summary>
    /// Apply a custom configuration delegate to the <see cref="MeterProviderBuilder"/>
    /// (e.g., to add views, processors, or additional exporters beyond the built-in helpers).
    /// </summary>
    public AsteriskOpenTelemetryBuilder ConfigureMetrics(Action<MeterProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _metricsConfigurators.Add(configure);
        return this;
    }
}
