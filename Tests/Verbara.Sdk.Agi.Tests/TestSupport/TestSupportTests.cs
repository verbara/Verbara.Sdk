using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Verbara.Sdk.Agi.Diagnostics;
using FluentAssertions;

namespace Verbara.Sdk.Agi.Tests.TestSupport;

/// <summary>
/// Pins what each test double does, so a test that uses one fails for the reason it names. A control
/// that asserts a counter did not move, or a socket stayed open, proves nothing if the double cannot
/// show the opposite.
/// </summary>
public sealed class TestSupportTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------------ accepted clients

    [Fact]
    public void UdpBacked_ShouldRejectNoDelayWithASocketException_WhenConfigured()
    {
        using var accepted = AcceptedClients.UdpBacked();

        var configure = () => accepted.Client.NoDelay = true;

        configure.Should().Throw<SocketException>()
            .Which.SocketErrorCode.Should().Be(SocketError.ProtocolOption, "setsockopt(TCP_NODELAY) on a UDP socket answers ENOPROTOOPT");
        accepted.IsSocketClosed.Should().BeFalse("the socket is open when it is handed over, so a server that drops it leaks it");
    }

    [Fact]
    public void DisposedSocket_ShouldRejectNoDelayWithObjectDisposed_WhenConfigured()
    {
        using var accepted = AcceptedClients.DisposedSocket();

        var configure = () => accepted.Client.NoDelay = true;

        configure.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void NeverConnected_ShouldAcceptNoDelayAndRejectGetStream_WhenHandedOver()
    {
        using var accepted = AcceptedClients.NeverConnected();

        var configure = () => accepted.Client.NoDelay = true;
        var readEndpoint = () => accepted.Client.Client.RemoteEndPoint;
        var getStream = () => accepted.Client.GetStream();

        configure.Should().NotThrow("the configure succeeds, so the failure lands one step later");
        readEndpoint.Should().NotThrow("FastAGI reads the remote endpoint in the same window");
        getStream.Should().Throw<InvalidOperationException>();
        accepted.IsSocketClosed.Should().BeFalse("the socket is open when it is handed over");
    }

    [Fact]
    public void Dispose_ShouldCloseTheSocket_WhenTheClientIsDisposed()
    {
        using var accepted = AcceptedClients.UdpBacked();

        accepted.Client.Dispose();

        accepted.IsSocketClosed.Should().BeTrue("IsSocketClosed reads the handle the client wraps");
    }

    [Fact]
    public async Task ParkUntilCancelledAsync_ShouldEndCancelled_WhenItsTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();

        var parked = AcceptedClients.ParkUntilCancelledAsync(cts.Token).AsTask();
        parked.IsCompleted.Should().BeFalse("nothing is returned until the token is cancelled");
        await cts.CancelAsync();

        await parked.Invoking(t => t.WaitAsync(SignalTimeout)).Should().ThrowAsync<OperationCanceledException>();
    }

    // -------------------------------------------------------------------------------- counters

    [Fact]
    public void Get_ShouldSumEachInstrumentOfTheNamedMeterOnly_WhenMeasurementsAreRecorded()
    {
        // A meter no SDK code uses, so this test moves no instrument another test counts.
        using var meter = new Meter($"TestSupport.{Guid.NewGuid():N}");
        using var otherMeter = new Meter($"TestSupport.{Guid.NewGuid():N}");
        using var counters = new AgiCounters(meter.Name);
        var accepted = meter.CreateCounter<long>(AgiCounters.ConnectionsAccepted);
        var failed = meter.CreateCounter<long>(AgiCounters.ScriptsFailed);
        var sameNameOtherMeter = otherMeter.CreateCounter<long>(AgiCounters.ScriptsFailed);

        accepted.Add(1);
        accepted.Add(1);
        failed.Add(1);
        sameNameOtherMeter.Add(10);

        counters.Accepted.Should().Be(2);
        counters.Failed.Should().Be(1);
        counters.Describe().Should().Be("accepted=2 failed=1 executed=0");
    }

    [Fact]
    public void Names_ShouldBeTheSdksAgiMeterAndInstruments_WhenTheCountersAreBuiltWithoutArguments()
    {
        AgiCounters.MeterName.Should().Be(AgiMetrics.Meter.Name);
        AgiCounters.ConnectionsAccepted.Should().Be(AgiMetrics.ConnectionsAccepted.Name);
        AgiCounters.ScriptsFailed.Should().Be(AgiMetrics.ScriptsFailed.Name);
        AgiCounters.ScriptsExecuted.Should().Be(AgiMetrics.ScriptsExecuted.Name);
        AgiCounters.ScriptsNotFound.Should().Be(AgiMetrics.ScriptsNotFound.Name);
        AgiCounters.Hangups.Should().Be(AgiMetrics.Hangups.Name);
        AgiCounters.ScriptDuration.Should().Be(AgiMetrics.ScriptDurationMs.Name);
    }

    [Fact]
    public async Task WhenRecorded_ShouldCompleteOnTheNthRecordOfAHistogramOnly_WhenRecordsArrive()
    {
        // A meter no SDK code uses, so this test moves no instrument another test counts.
        using var meter = new Meter($"TestSupport.{Guid.NewGuid():N}");
        using var counters = new AgiCounters(meter.Name);
        var duration = meter.CreateHistogram<double>(AgiCounters.ScriptDuration);
        var notFound = meter.CreateCounter<long>(AgiCounters.ScriptsNotFound);

        var third = counters.WhenRecorded(AgiCounters.ScriptDuration, 3);
        duration.Record(12.5);
        duration.Record(0);
        notFound.Add(5);
        var completedAfterTwo = third.IsCompleted;
        duration.Record(7);
        await third.WaitAsync(SignalTimeout);

        completedAfterTwo.Should().BeFalse("two records of the histogram, and one of another instrument, are not three");
        counters.Records(AgiCounters.ScriptDuration).Should().Be(3, "a histogram's records are counted, not its values summed");
        counters.Get(AgiCounters.ScriptsNotFound).Should().Be(5, "a counter is still summed");
        counters.Records(AgiCounters.ScriptsNotFound).Should().Be(1);
        counters.WhenRecorded(AgiCounters.ScriptDuration, 3).IsCompleted.Should().BeTrue(
            "a count already reached completes at once");
    }
}
