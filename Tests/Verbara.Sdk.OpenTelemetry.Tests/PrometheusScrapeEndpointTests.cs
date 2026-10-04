using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Scrapes the Prometheus endpoint on a real Kestrel host after recording a long counter and a double histogram.
/// A long-only scrape is not enough: the defect this guards against served long counters and emptied the whole
/// response as soon as a double-valued instrument had a value.
/// </summary>
public sealed class PrometheusScrapeEndpointTests
{
    private const string PrometheusAccept =
        "application/openmetrics-text;version=1.0.0;q=0.5,application/openmetrics-text;version=0.0.1;q=0.4,text/plain;version=0.0.4;q=0.3,*/*;q=0.2";

    [Theory]
    [InlineData(null)]
    [InlineData(PrometheusAccept)]
    public async Task ScrapingEndpoint_ShouldServeEveryRecordedInstrument_WhenAHistogramHasBeenRecorded(string? accept)
    {
        var meterName = "verbara.test.scrape." + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVerbaraOpenTelemetry(b => b.AddMeter(meterName).WithPrometheusExporter());
        await using var app = builder.Build();
        app.UseOpenTelemetryPrometheusScrapingEndpoint();
        await app.StartAsync();

        using var meter = new Meter(meterName);
        meter.CreateCounter<long>("scrape_probe_long").Add(7);
        meter.CreateHistogram<double>("scrape_probe_seconds").Record(0.25);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Urls.First() + "/metrics"));
        if (accept is not null)
            request.Headers.TryAddWithoutValidation("Accept", accept);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        using (new AssertionScope())
        {
            ((int)response.StatusCode).Should().Be(200);
            body.Length.Should().BeGreaterThan(0, "a 200 with an empty body is the defect");
            body.Should().Contain($"scrape_probe_long_total{{otel_scope_name=\"{meterName}\"}} 7", "the long counter's sample was recorded");
            body.Should().Contain($"scrape_probe_seconds_count{{otel_scope_name=\"{meterName}\"}} 1", "the double histogram's count was recorded");
            body.Should().Contain(string.Create(CultureInfo.InvariantCulture, $"scrape_probe_seconds_sum{{otel_scope_name=\"{meterName}\"}} 0.25"));
        }

        await app.StopAsync();
    }

    [Fact]
    public async Task ScrapingEndpoint_ShouldKeepServingTheCounter_WhenAHistogramStartsRecordingAfterIt()
    {
        var meterName = "verbara.test.scrape." + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The exporter caches a scrape response (300 ms by default); a second scrape must render anew.
        builder.Services.Configure<global::OpenTelemetry.Exporter.PrometheusAspNetCoreOptions>(o => o.ScrapeResponseCacheDurationMilliseconds = 0);
        builder.Services.AddVerbaraOpenTelemetry(b => b.AddMeter(meterName).WithPrometheusExporter());
        await using var app = builder.Build();
        app.UseOpenTelemetryPrometheusScrapingEndpoint();
        await app.StartAsync();
        using var http = new HttpClient();
        var url = new Uri(app.Urls.First() + "/metrics");

        using var meter = new Meter(meterName);
        meter.CreateCounter<long>("scrape_probe_long").Add(7);
        var before = await http.GetStringAsync(url);
        meter.CreateHistogram<double>("scrape_probe_seconds").Record(0.25);
        var after = await http.GetStringAsync(url);

        using (new AssertionScope())
        {
            before.Should().Contain($"scrape_probe_long_total{{otel_scope_name=\"{meterName}\"}} 7", "the counter alone is served");
            after.Length.Should().BeGreaterThan(0, "a double histogram must not empty the response");
            after.Should().Contain($"scrape_probe_long_total{{otel_scope_name=\"{meterName}\"}} 7", "the counter is still recorded");
            after.Should().Contain($"scrape_probe_seconds_count{{otel_scope_name=\"{meterName}\"}} 1", "the histogram was recorded");
            after.Should().Contain(string.Create(CultureInfo.InvariantCulture, $"scrape_probe_seconds_sum{{otel_scope_name=\"{meterName}\"}} 0.25"));
        }

        await app.StopAsync();
    }
}
