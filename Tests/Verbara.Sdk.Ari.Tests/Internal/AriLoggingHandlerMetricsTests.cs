using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Ari.Tests.TestSupport;

namespace Verbara.Sdk.Ari.Tests.Internal;

/// <summary>
/// The ARI REST instruments are recorded by the one handler every ARI REST request passes through: one
/// <c>ari.rest.requests.sent</c> per request issued, one <c>ari.rest.roundtrip</c> per request that receives a
/// response, whatever its status, and none for a request that fails without one. The tests drive a request through
/// <see cref="AriLoggingHandler"/> and read the meter by name; none of them records into an instrument itself.
/// </summary>
[Collection(AriRestMetricsGroup.Name)]
public sealed class AriLoggingHandlerMetricsTests
{
    private const string MeterName = "Verbara.Sdk.Ari";
    private const string SentName = "ari.rest.requests.sent";
    private const string RoundtripName = "ari.rest.roundtrip";

    [Fact]
    public async Task SendAsync_ShouldCountOneRequestAndRecordOneRoundtrip_WhenTheRequestIsAnswered()
    {
        using var meter = new RestMeasurements();
        using var client = CreateClient(NullLogger.Instance, new StubHandler(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(30)));

        var response = await client.GetAsync("/ari/channels");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        meter.Sent.Should().Equal(1L);
        meter.Roundtrips.Should().ContainSingle()
            .Which.Should().BeGreaterThanOrEqualTo(20d, "the roundtrip is the elapsed time of a request the inner handler held for 30 ms");
    }

    [Fact]
    public async Task SendAsync_ShouldCountOneRequestAndRecordOneRoundtrip_WhenTheRequestIsAnsweredWithAnErrorStatus()
    {
        using var meter = new RestMeasurements();
        using var client = CreateClient(NullLogger.Instance, new StubHandler(HttpStatusCode.InternalServerError));

        var response = await client.PostAsync("/ari/channels", null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        meter.Sent.Should().Equal(1L);
        meter.Roundtrips.Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(0d);
    }

    [Fact]
    public async Task SendAsync_ShouldCountTheRequestAndRecordNoRoundtrip_WhenTheTransportThrowsBeforeAResponse()
    {
        using var meter = new RestMeasurements();
        var refused = new HttpRequestException("connection refused");
        using var client = CreateClient(NullLogger.Instance, new StubHandler(refused));

        var act = () => client.DeleteAsync("/ari/channels/c-1");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(refused);
        meter.Sent.Should().Equal(1L);
        meter.Roundtrips.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldRecordTheSameAsWithLoggingOn_WhenEveryLogLevelIsDisabled()
    {
        var on = await MeasureOneAnsweredRequest(new EverythingEnabledLogger());
        var off = await MeasureOneAnsweredRequest(NullLogger.Instance);

        on.Sent.Should().Equal(1L);
        on.Roundtrips.Should().ContainSingle();
        off.Sent.Should().Equal(on.Sent);
        off.Roundtrips.Should().HaveSameCount(on.Roundtrips);
    }

    private static async Task<(IReadOnlyList<long> Sent, IReadOnlyList<double> Roundtrips)> MeasureOneAnsweredRequest(ILogger logger)
    {
        using var meter = new RestMeasurements();
        using var client = CreateClient(logger, new StubHandler(HttpStatusCode.OK));

        (await client.GetAsync("/ari/asterisk/info")).StatusCode.Should().Be(HttpStatusCode.OK);

        return (meter.Sent, meter.Roundtrips);
    }

    private static HttpClient CreateClient(ILogger logger, HttpMessageHandler inner) =>
        new(new AriLoggingHandler(logger) { InnerHandler = inner }) { BaseAddress = new Uri("http://localhost:8088") };

    /// <summary>An inner handler that answers with a status after an optional delay, or throws before any response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly TimeSpan _delay;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, TimeSpan delay = default)
        {
            _status = status;
            _delay = delay;
        }

        public StubHandler(Exception failure) => _failure = failure;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failure is not null)
                throw _failure;
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken); // fence-allow: SIMULATED-WORK — Asterisk taking this long to answer, which the roundtrip must measure
            return new HttpResponseMessage(_status) { Content = new StringContent("body") };
        }
    }

    /// <summary>A logger with every level enabled, so the handler's log calls all run.</summary>
    private sealed class EverythingEnabledLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _ = formatter(state, exception);
    }

    /// <summary>
    /// The REST instruments' measurements while alive, matched by the meter's name and each instrument's name — the
    /// contract an exporter sees — never by a reference to the SDK's field.
    /// </summary>
    private sealed class RestMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<long> _sent = new();
        private readonly ConcurrentQueue<double> _roundtrips = new();

        public RestMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
                    && (string.Equals(instrument.Name, SentName, StringComparison.Ordinal)
                        || string.Equals(instrument.Name, RoundtripName, StringComparison.Ordinal)))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (string.Equals(instrument.Name, SentName, StringComparison.Ordinal))
                    _sent.Enqueue(value);
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (string.Equals(instrument.Name, RoundtripName, StringComparison.Ordinal))
                    _roundtrips.Enqueue(value);
            });
            _listener.Start();
        }

        /// <summary>Every <c>ari.rest.requests.sent</c> increment so far.</summary>
        public IReadOnlyList<long> Sent => [.. _sent];

        /// <summary>Every <c>ari.rest.roundtrip</c> measurement so far, in milliseconds.</summary>
        public IReadOnlyList<double> Roundtrips => [.. _roundtrips];

        public void Dispose() => _listener.Dispose();
    }
}
