using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.RegularExpressions;
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
/// The two session counters nothing in the SDK records, <c>sessions.timed_out</c> and <c>sessions.orphaned</c>: each
/// field is obsolete with its own diagnostic id, so a consumer's build says the instrument is not emitted before a
/// dashboard does, and each instrument is still published on the meter, so deprecating it removes nothing.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed partial class SessionMetricsDeprecationTests : IAsyncLifetime
{
    private const string MeterName = "Verbara.Sdk.Sessions";
    private const string ServerId = "srv-1";

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sut;

    public SessionMetricsDeprecationTests()
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

    [Theory]
    [InlineData("SessionsTimedOut", "sessions.timed_out")]
    [InlineData("SessionsOrphaned", "sessions.orphaned")]
    public void Field_ShouldBeObsoleteWithVsdk0006SayingItIsNotEmittedAndNamingNoVersion_WhenAConsumerReadsIt(
        string fieldName, string instrumentName)
    {
        // Reading the attribute is the point: a consumer building with TreatWarningsAsErrors gets VSDK0006, not a
        // blanket CS0618, and the message says the instrument is never recorded, without promising when it goes.
        var field = typeof(SessionMetrics).GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        field.Should().NotBeNull("the field stays published; deprecating it removes nothing");

        var obsolete = field!.GetCustomAttribute<ObsoleteAttribute>();

        obsolete.Should().NotBeNull($"nothing in the SDK records {instrumentName}, so the field is deprecated");
        obsolete!.DiagnosticId.Should().Be("VSDK0006");
        obsolete.IsError.Should().BeFalse("a deprecation warns; it does not break a build without TreatWarningsAsErrors");
        obsolete.Message.Should().ContainEquivalentOf("not emitted").And.Contain(instrumentName);
        VersionLike().IsMatch(obsolete.Message!).Should().BeFalse(
            "the message names no version (no digit-dot-digit): a removal is a later ruling, not a promise made here");
        YearLike().IsMatch(obsolete.Message!).Should().BeFalse("the message names no date");
    }

    [Fact]
    public void Fields_ShouldStillBePublishedAndRecordNothing_WhenACallRunsToCompletion()
    {
        // The non-removal promise: the deprecated fields still compile, their instruments are still published on
        // the meter under the same names, and running a call to its end records nothing on either. The listener
        // proves itself live on sessions.completed, counted only on this thread (other classes run in parallel);
        // the two deprecated instruments are counted from every thread, since nothing in the SDK may record them.
        var meter = SessionMetrics.Meter;
        var thread = Environment.CurrentManagedThreadId;
        var published = new ConcurrentDictionary<string, Instrument>(StringComparer.Ordinal);
        var deprecatedMeasurements = new ConcurrentQueue<string>();
        var completedOnThisThread = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (!ReferenceEquals(instrument.Meter, meter))
                return;
            published[instrument.Name] = instrument;
            if (instrument.Name is "sessions.timed_out" or "sessions.orphaned" or "sessions.completed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "sessions.completed")
            {
                if (Environment.CurrentManagedThreadId == thread)
                    Interlocked.Add(ref completedOnThisThread, value);
            }
            else
            {
                deprecatedMeasurements.Enqueue(instrument.Name);
            }
        });
        listener.Start();

#pragma warning disable VSDK0006 // reading the obsolete fields is the point of this test: they must keep compiling
        var timedOut = SessionMetrics.SessionsTimedOut;
        var orphaned = SessionMetrics.SessionsOrphaned;
#pragma warning restore VSDK0006

        EndCall("p");

        new
        {
            TimedOutPublished = published.TryGetValue("sessions.timed_out", out var t) && ReferenceEquals(t, timedOut),
            OrphanedPublished = published.TryGetValue("sessions.orphaned", out var o) && ReferenceEquals(o, orphaned),
            Completed = Interlocked.Read(ref completedOnThisThread),
            DeprecatedMeasurements = deprecatedMeasurements.ToArray(),
        }.Should().BeEquivalentTo(
            new { TimedOutPublished = true, OrphanedPublished = true, Completed = 1L, DeprecatedMeasurements = Array.Empty<string>() },
            "both deprecated instruments stay published on the Verbara.Sdk.Sessions meter, and a call that ends is "
            + "counted in sessions.completed and in neither of them");
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

    [GeneratedRegex(@"\d\.\d")]
    private static partial Regex VersionLike();

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearLike();
}
