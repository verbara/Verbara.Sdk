using System.Threading;
using Asterisk.Sdk;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.IntegrationTests.Live;

[Collection("Integration")]
[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal is handled via IAsyncLifetime.DisposeAsync")]
public class LiveServerIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private Asterisk.Sdk.Ami.Connection.AmiConnection? _connection;
    private AsteriskServer? _server;

    public LiveServerIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connection = AsteriskFixture.CreateAmiConnection(_fixture);
        await _connection.ConnectAsync();

        _server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [Fact]
    public void AsteriskServer_ShouldStartAndLoadState()
    {
        // Server should be running with managers initialized
        _server!.Channels.Should().NotBeNull();
        _server.Queues.Should().NotBeNull();
        _server.Agents.Should().NotBeNull();
    }

    [Fact]
    public void AsteriskServer_ShouldExposeMetrics()
    {
        // Verify LiveMetrics registers observable instruments after server start
        long channelGaugeObserved = -1;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Asterisk.Sdk.Live")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "live.channels.active")
                Interlocked.Exchange(ref channelGaugeObserved, measurement);
        });
        listener.Start();
        listener.RecordObservableInstruments();

        // The gauge should have been observed (value >= 0, typically 0 on a fresh Asterisk)
        Interlocked.Read(ref channelGaugeObserved).Should().BeGreaterOrEqualTo(0,
            "live.channels.active gauge should be observable after server start");
    }
}
