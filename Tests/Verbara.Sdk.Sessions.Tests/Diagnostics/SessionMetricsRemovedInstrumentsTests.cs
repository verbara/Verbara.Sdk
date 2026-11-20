using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Diagnostics;

/// <summary>
/// The two session counters nothing in the SDK recorded since 2.7.0, <c>sessions.timed_out</c> and
/// <c>sessions.orphaned</c>, left the meter in 2.11.0 with the fields that were their only creation. A call that ends is
/// counted in <c>sessions.completed</c> or <c>sessions.failed</c>.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class SessionMetricsRemovedInstrumentsTests : IAsyncLifetime
{
    private const string MeterName = "Verbara.Sdk.Sessions";
    private const string ServerId = "srv-1";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;

    public SessionMetricsRemovedInstrumentsTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _sut = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
        _sut.AttachToServer(_server, ServerId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void RemovedInstruments_ShouldNotBePublished_WhenACallRunsToCompletion()
    {
        // The listener proves itself live on sessions.completed, counted only on this thread (other classes run in
        // parallel); the two removed names are looked for among every instrument the meter publishes.
        var meter = SessionMetrics.Meter;
        var thread = Environment.CurrentManagedThreadId;
        var published = new ConcurrentDictionary<string, Instrument>(StringComparer.Ordinal);
        var completedOnThisThread = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (!ReferenceEquals(instrument.Meter, meter))
                return;
            published[instrument.Name] = instrument;
            if (instrument.Name == "sessions.completed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
                Interlocked.Add(ref completedOnThisThread, value);
        });
        listener.Start();

        EndCall("p");

        new
        {
            TimedOutPublished = published.ContainsKey("sessions.timed_out"),
            OrphanedPublished = published.ContainsKey("sessions.orphaned"),
            CompletedPublished = published.ContainsKey("sessions.completed"),
            FailedPublished = published.ContainsKey("sessions.failed"),
            Completed = Interlocked.Read(ref completedOnThisThread),
        }.Should().BeEquivalentTo(
            new { TimedOutPublished = false, OrphanedPublished = false, CompletedPublished = true, FailedPublished = true, Completed = 1L },
            "2.11.0 removed both instruments from the Verbara.Sdk.Sessions meter, and a call that ends is counted in "
            + "sessions.completed");
        meter.Name.Should().Be(MeterName);
    }

    /// <summary>Two legs sharing <c>L-{tag}</c>, dialled, answered and both hung up: one completed call.</summary>
    private void EndCall(string tag)
    {
        var linkedId = $"L-{tag}";
        _server.Channels.OnNewChannel($"c-{tag}", $"PJSIP/trunk-c-{tag}", ChannelState.Ring,
            context: "from-trunk", linkedId: linkedId);
        _server.Channels.OnNewChannel($"a-{tag}", $"PJSIP/100-a-{tag}", ChannelState.Ring, linkedId: linkedId);
        _server.Channels.OnDialBegin($"c-{tag}", $"a-{tag}", $"PJSIP/100-a-{tag}", null);
        _server.Channels.OnNewState($"a-{tag}", ChannelState.Up);
        var session = _sut.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"premise: a session was opened for '{linkedId}'");

        _server.Channels.OnHangup($"a-{tag}", HangupCause.NormalClearing);
        _server.Channels.OnHangup($"c-{tag}", HangupCause.NormalClearing);
        session.State.Should().Be(CallSessionState.Completed, $"premise: '{tag}' ended with both legs hung up");
    }
}
