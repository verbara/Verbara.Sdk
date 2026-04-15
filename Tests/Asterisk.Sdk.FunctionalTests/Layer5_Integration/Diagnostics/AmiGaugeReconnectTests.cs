namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Diagnostics;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Regression tests for the AMI observable-gauge re-registration bug
/// originally documented in the v1.5.x audit (see
/// docs/superpowers/artifacts/2026-04-13-ami-gauge-audit.md).
///
/// <para>
/// Bug history: prior to the <c>_gaugesRegistered</c> guard added in
/// <c>AmiConnection.ConnectAsync</c>, every reconnect created a brand-new
/// <see cref="Meter.CreateObservableGauge{T}(string,System.Func{T},string?,string?)"/>
/// instance whose closure captured a stale <c>this</c>. After N reconnects,
/// each <c>RecordObservableInstruments()</c> cycle observed N values per
/// gauge name (the live one + N-1 stale callbacks pinned by the static
/// <see cref="Asterisk.Sdk.Ami.Diagnostics.AmiMetrics.Meter"/>).
/// </para>
///
/// <para>
/// These tests pin that contract: after multiple reconnects the gauges still
/// emit exactly one live observation per name and the values continue to
/// reflect real connection state (not stale snapshots).
/// </para>
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AmiGaugeReconnectTests : FunctionalTestBase
{
    private const string PendingActionsGauge = "ami.pending_actions";
    private const string PendingEventsGauge = "ami.event_pump.pending";

    [Fact]
    public async Task AmiObservableGauges_ShouldEmitLiveValues_AfterMultipleReconnects()
    {
        // Arrange
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = true;
            opts.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
        });

        await connection.ConnectAsync();
        connection.State.Should().Be(AmiConnectionState.Connected);

        // Act: 3 reconnect cycles. Each cycle = restart container + wait for Reconnected event.
        const int reconnectCycles = 3;
        for (var cycle = 1; cycle <= reconnectCycles; cycle++)
        {
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler() => reconnected.TrySetResult();
            connection.Reconnected += Handler;

            try
            {
                await DockerControl.RestartContainerAsync();
                await DockerControl.WaitForHealthyAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                cts.Token.Register(() => reconnected.TrySetCanceled());
                await reconnected.Task;
            }
            finally
            {
                connection.Reconnected -= Handler;
            }

            connection.State.Should().Be(AmiConnectionState.Connected,
                $"connection should be live after reconnect cycle {cycle}");

            // Sanity: connection still functional after reconnect
            var pong = await connection.SendActionAsync(new PingAction());
            pong.Response.Should().Be("Success",
                $"PingAction must succeed after reconnect cycle {cycle}");
        }

        // Assert: take a single ObservableInstrument snapshot and verify
        //   (a) exactly one observation per gauge (no duplicate registrations),
        //   (b) values are non-negative numbers (callbacks read live state).
        var snapshot = SnapshotObservableGauges(TimeSpan.FromMilliseconds(500));

        snapshot.Should().ContainKey(PendingActionsGauge,
            "pending_actions gauge must continue emitting after reconnects");
        snapshot.Should().ContainKey(PendingEventsGauge,
            "event_pump.pending gauge must continue emitting after reconnects");

        snapshot[PendingActionsGauge].Should().HaveCount(1,
            "duplicate observations indicate the gauge was re-registered on reconnect (the original bug)");
        snapshot[PendingEventsGauge].Should().HaveCount(1,
            "duplicate observations indicate the gauge was re-registered on reconnect (the original bug)");

        snapshot[PendingActionsGauge][0].Should().BeGreaterThanOrEqualTo(0)
            .And.NotBe(double.NaN);
        snapshot[PendingEventsGauge][0].Should().BeGreaterThanOrEqualTo(0)
            .And.NotBe(double.NaN);
    }

    /// <summary>
    /// Forces a single observation cycle on the AMI Meter and returns the list of
    /// values reported per gauge name. The bug under test would yield N values
    /// per name after N reconnects; the fix yields exactly 1.
    /// </summary>
    private static Dictionary<string, List<double>> SnapshotObservableGauges(TimeSpan settleDelay)
    {
        var observations = new ConcurrentDictionary<string, List<double>>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Asterisk.Sdk.Ami"
                && (instrument.Name == PendingActionsGauge || instrument.Name == PendingEventsGauge))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
            observations.GetOrAdd(inst.Name, _ => []).Add(value));
        listener.SetMeasurementEventCallback<int>((inst, value, _, _) =>
            observations.GetOrAdd(inst.Name, _ => []).Add(value));
        listener.SetMeasurementEventCallback<double>((inst, value, _, _) =>
            observations.GetOrAdd(inst.Name, _ => []).Add(value));
        listener.Start();

        // Let asynchronous connection state settle so callbacks read coherent values.
        Thread.Sleep(settleDelay);

        // Single observation cycle — any duplicate callbacks registered by the
        // (now-fixed) reconnect path would each fire once here.
        listener.RecordObservableInstruments();

        return observations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}
