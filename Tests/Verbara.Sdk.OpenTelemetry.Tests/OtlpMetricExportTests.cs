using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Exports a long counter and a double histogram through <c>WithOtlpExporter</c> to a real loopback receiver.
/// The OTLP exporter serializes metric points through internals of the OpenTelemetry core package, so a core
/// version the exporter was not built against can fail every batch that holds a histogram. The exporter
/// catches that, writes it only to the self-diagnostics log and sends nothing, so the only visible symptom
/// is a receiver that never gets a metrics request.
/// </summary>
public sealed class OtlpMetricExportTests
{
    [Fact]
    public async Task WithOtlpExporter_ShouldDeliverTheHistogramBatch_WhenMetricsAreFlushed()
    {
        var meterName = "verbara.test.otlp." + Guid.NewGuid().ToString("N");
        var bodies = new ConcurrentQueue<byte[]>();

        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.Logging.ClearProviders();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = receiverBuilder.Build();
        receiver.Run(async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            bodies.Enqueue(buffer.ToArray());
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
        await receiver.StartAsync();
        var endpoint = new Uri(receiver.Urls.First() + "/v1/metrics");

        var services = new ServiceCollection();
        services.AddVerbaraOpenTelemetry(b => b.AddMeter(meterName).WithOtlpExporter(o =>
        {
            o.Endpoint = endpoint;
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
        }));
        await using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();

        using var meter = new Meter(meterName);
        meter.CreateCounter<long>("otlp_probe_long").Add(7);
        meter.CreateHistogram<double>("otlp_probe_seconds").Record(0.25);

        var flushed = meterProvider.ForceFlush(10_000);
        var delivered = bodies.Select(b => Encoding.UTF8.GetString(b)).FirstOrDefault(s => s.Contains(meterName, StringComparison.Ordinal));

        using (new AssertionScope())
        {
            flushed.Should().BeTrue("the flush must complete within its timeout");
            delivered.Should().NotBeNull("a batch the exporter could not serialize is dropped without a request");
            delivered.Should().Contain("otlp_probe_long", "the counter is in the batch");
            delivered.Should().Contain("otlp_probe_seconds", "the histogram is in the batch");
        }

        await receiver.StopAsync();
    }
}
