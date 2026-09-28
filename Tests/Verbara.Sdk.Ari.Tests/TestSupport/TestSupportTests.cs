using System.Diagnostics.Metrics;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

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
        var getStream = () => accepted.Client.GetStream();

        configure.Should().NotThrow("the configure succeeds, so the failure lands one step later");
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

    // ------------------------------------------------------------------------- scripted stream

    [Fact]
    public async Task ReadAsync_ShouldReturnEachChunkThenThrow_WhenAnExceptionIsScripted()
    {
        var failure = new IOException("reset", new SocketException((int)SocketError.ConnectionReset));
        await using var stream = new ScriptedStream([[1, 2, 3], [4, 5]], failure);
        var buffer = new byte[16];

        (await stream.ReadAsync(buffer)).Should().Be(3);
        buffer[..3].Should().Equal(1, 2, 3);
        (await stream.ReadAsync(buffer)).Should().Be(2);
        buffer[..2].Should().Equal(4, 5);
        stream.ScriptSpent.IsCompleted.Should().BeFalse("no read has gone past the script yet");

        var third = async () => await stream.ReadAsync(buffer);

        (await third.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        stream.ScriptSpent.IsCompleted.Should().BeTrue();
        stream.Reads.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnEndOfStream_WhenNoExceptionIsScripted()
    {
        await using var stream = new ScriptedStream([7], then: null);
        var buffer = new byte[16];

        (await stream.ReadAsync(buffer)).Should().Be(1);
        (await stream.ReadAsync(buffer)).Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_ShouldFailOnTheFirstRead_WhenTheScriptIsEmpty()
    {
        var failure = new IOException("reset before anything was sent");
        await using var stream = new ScriptedStream(first: [], failure);

        var first = async () => await stream.ReadAsync(new byte[16]);

        (await first.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadAsync_ShouldHoldTheFailureUntilReleased_WhenAReleaseIsGiven()
    {
        var failure = new IOException("reset");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new ScriptedStream([[1]], failure, release.Task);
        var buffer = new byte[16];
        (await stream.ReadAsync(buffer)).Should().Be(1);

        var held = stream.ReadAsync(buffer).AsTask();
        await stream.ScriptSpent.WaitAsync(SignalTimeout);
        held.IsCompleted.Should().BeFalse("the read past the script waits for its release");
        release.SetResult();

        (await held.Invoking(t => t.WaitAsync(SignalTimeout)).Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task ReadAsync_ShouldEndCancelled_WhenTheReaderCancelsAHeldRead()
    {
        using var cts = new CancellationTokenSource();
        await using var stream = new ScriptedStream([], new IOException("never thrown"), new TaskCompletionSource().Task);

        var held = stream.ReadAsync(new byte[16], cts.Token).AsTask();
        await stream.ScriptSpent.WaitAsync(SignalTimeout);
        await cts.CancelAsync();

        await held.Invoking(t => t.WaitAsync(SignalTimeout)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposeAsync_ShouldMarkTheStreamDisposed_WhenTheReaderDisposesIt()
    {
        var stream = new ScriptedStream([1], then: null);
        stream.IsDisposed.Should().BeFalse();

        await stream.DisposeAsync();

        stream.IsDisposed.Should().BeTrue();
    }

    // ----------------------------------------------------------------- transport-failure counter

    [Fact]
    public void Total_ShouldCountOnlyTheNamedInstrumentOfTheNamedMeter_WhenMeasurementsAreRecorded()
    {
        // A meter no SDK code uses, so this test moves no instrument another test counts.
        using var meter = new Meter($"TestSupport.{Guid.NewGuid():N}");
        using var counter = new TransportFailureCounter(meter.Name, TransportFailureCounter.InstrumentName);
        using var otherMeter = new Meter($"TestSupport.{Guid.NewGuid():N}");
        var counted = meter.CreateCounter<long>(TransportFailureCounter.InstrumentName);
        var sameNameOtherMeter = otherMeter.CreateCounter<long>(TransportFailureCounter.InstrumentName);
        var otherName = meter.CreateCounter<long>("audio.streams.closed");

        counted.Add(1);
        counted.Add(2);
        sameNameOtherMeter.Add(10);
        otherName.Add(100);

        counter.Total.Should().Be(3);
        counter.Measurements.Should().Be(2);
    }

    [Fact]
    public void MeterName_ShouldBeTheSdksAudioMeter_WhenTheCounterIsBuiltWithoutArguments()
    {
        TransportFailureCounter.MeterName.Should().Be(Verbara.Sdk.Ari.Diagnostics.AudioStreamMetrics.Meter.Name);
    }

    // ------------------------------------------------------------------------ capturing logger

    private static readonly Action<ILogger, string, Exception?> TransportFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(TransportFailed)),
            "The transport under channel_id={ChannelId} failed");

    private static readonly Action<ILogger, Exception?> Unrelated =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(Unrelated)), "not this one");

    [Fact]
    public void Log_ShouldKeepTheLevelEventValuesAndTheExceptionItself_WhenAnEntryIsWritten()
    {
        var logger = new CapturingLogger<TestSupportTests>();
        var failure = new IOException("reset");

        TransportFailed(logger, "chan-1", failure);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventName.Should().Be("TransportFailed");
        entry.Value("ChannelId").Should().Be("chan-1");
        entry.Exception.Should().BeSameAs(failure);
        entry.Message.Should().Be("The transport under channel_id=chan-1 failed");
    }

    [Fact]
    public async Task WhenLogged_ShouldCompleteWithTheEntry_WhenAMatchingEntryIsWrittenLater()
    {
        var logger = new CapturingLogger<TestSupportTests>();
        var waiting = logger.WhenLogged(e => e.Level == LogLevel.Warning);
        Unrelated(logger, null);
        waiting.IsCompleted.Should().BeFalse("an entry that does not match does not end the wait");

        TransportFailed(logger, "chan-2", null);

        var entry = await waiting.WaitAsync(SignalTimeout);
        entry.Value("ChannelId").Should().Be("chan-2");
    }

    [Fact]
    public async Task WhenLogged_ShouldCompleteAtOnce_WhenAMatchingEntryWasAlreadyWritten()
    {
        var logger = new CapturingLogger<TestSupportTests>();
        TransportFailed(logger, "chan-3", null);

        var entry = await logger.WhenLogged(e => e.EventName == "TransportFailed").WaitAsync(SignalTimeout);

        entry.Value("ChannelId").Should().Be("chan-3");
    }
}
