namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Cdr;

using System.Globalization;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for CdrEvent (Call Detail Record). CDR events fire after call hangup
/// and require cdr_manager.conf enabled in Asterisk.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class CdrEventTests : FunctionalTestBase
{
    public CdrEventTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task AnsweredCall_ShouldProduceCdrEvent_WithCorrectFields()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var cdrReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.Subscribe(new CdrObserver(cdrReceived));

        // ext 101 = Answer + Wait(5) + Hangup
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-answered-01"
        });

        // CDR fires after hangup; Wait(5) + processing time = up to 30s
        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        result.Should().Be(cdrReceived.Task, "ext 101 answers, waits 5 s and hangs up, so cdr_manager must report a Cdr event within 30 s");

        var cdr = await cdrReceived.Task;
        cdr.Channel.Should().NotBeNullOrEmpty("CDR must include the originating channel");
        cdr.Disposition.Should().Be("ANSWERED", "ext 101 answers the call");
        cdr.Duration.Should().BeGreaterThan(0, "answered call must have a non-zero duration");
        cdr.BillableSeconds.Should().BeGreaterThanOrEqualTo(0, "billable seconds must be non-negative");

        cdr.StartTime.Should().NotBeNullOrEmpty("CDR must include a start timestamp");

        // Verify StartTime parses as a valid date
        var parsed = DateTimeOffset.TryParse(
            cdr.StartTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var startDate);
        parsed.Should().BeTrue("StartTime '{0}' must be parseable as DateTimeOffset", cdr.StartTime);
        startDate.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5), "call must have started recently");
    }

    [Fact]
    public async Task UnansweredCall_ShouldProduceCdr_WithFailedDisposition()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var cdrReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Only this call's CDRs: both halves of the Local channel this test originates are named after ext 131.
        using var subscription = connection.Subscribe(new FilteredCdrObserver(
            cdr => cdr.Channel?.StartsWith("Local/131@test-functional-", StringComparison.Ordinal) ?? false,
            cdrReceived));

        // ext 131 runs Congestion() without answering, so the call is created and ends unanswered. Measured on
        // Asterisk 23: both halves' CDRs carry Disposition FAILED (Busy() gives BUSY, a ring timeout NO ANSWER).
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/131@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000,
            ActionId = "cdr-unanswered-01"
        });

        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        result.Should().Be(cdrReceived.Task, "ext 131 runs Congestion() on a channel it never answers, so cdr_manager must report its Cdr within 15 s");

        var cdr = await cdrReceived.Task;
        cdr.Disposition.Should().Be("FAILED", "a call that ends in Congestion() without being answered is a failed call");
        cdr.AnswerTime.Should().BeNullOrEmpty("the call was never answered");
        (cdr.BillableSeconds ?? 0).Should().Be(0, "an unanswered call has no billable seconds");
    }

    [Fact]
    public async Task CdrTimestamps_ShouldBeChronologicalStrings()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var cdrReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.Subscribe(new CdrObserver(cdrReceived));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-timestamps-01"
        });

        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        result.Should().Be(cdrReceived.Task, "ext 101 answers, waits 5 s and hangs up, so cdr_manager must report a Cdr event within 30 s");

        var cdr = await cdrReceived.Task;

        // Both StartTime and EndTime must be present and parseable
        cdr.StartTime.Should().NotBeNullOrEmpty("CDR must contain StartTime");
        cdr.EndTime.Should().NotBeNullOrEmpty("CDR must contain EndTime");

        var startParsed = DateTimeOffset.TryParse(
            cdr.StartTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var startDate);
        var endParsed = DateTimeOffset.TryParse(
            cdr.EndTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var endDate);

        startParsed.Should().BeTrue("StartTime must be a valid timestamp");
        endParsed.Should().BeTrue("EndTime must be a valid timestamp");

        startDate.Should().BeOnOrBefore(endDate, "EndTime must not precede StartTime");

        // Duration should approximately match EndTime - StartTime (±2s tolerance)
        cdr.Duration.Should().NotBeNull("a Cdr event for an answered call must carry Duration");
        var clockDuration = (endDate - startDate).TotalSeconds;
        clockDuration.Should().BeApproximately(cdr.Duration!.Value, 2.0,
            "CDR Duration must be within 2 seconds of EndTime - StartTime");
    }

    [Fact]
    public async Task CdrDisposition_ShouldMatchCallOutcome()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var answeredReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each outcome is read from the CDR of its own call, named after the extension it dialled.
        using var subscription = connection.Subscribe(new ClassifyingCdrObserver(
            cdr =>
            {
                if (cdr.Channel?.StartsWith("Local/101@test-functional-", StringComparison.Ordinal) == true)
                    answeredReceived.TrySetResult(cdr);
                else if (cdr.Channel?.StartsWith("Local/130@test-functional-", StringComparison.Ordinal) == true)
                    busyReceived.TrySetResult(cdr);
            }));

        // An answered call: ext 101 answers, waits 5 s and hangs up.
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/101@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-disposition-answered"
        });

        // A busy call: ext 130 runs Busy() without answering.
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/130@test-functional",
            Context = "test-functional",
            Exten = "101",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000,
            ActionId = "cdr-disposition-busy"
        });

        var busyResult = await Task.WhenAny(busyReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        busyResult.Should().Be(busyReceived.Task, "ext 130 runs Busy() on a channel it never answers, so cdr_manager must report its Cdr within 15 s");

        var answeredResult = await Task.WhenAny(answeredReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        answeredResult.Should().Be(answeredReceived.Task, "ext 101 answers, waits 5 s and hangs up, so cdr_manager must report its Cdr within 30 s");

        (await answeredReceived.Task).Disposition.Should().Be("ANSWERED", "ext 101 answers the call");
        (await busyReceived.Task).Disposition.Should().Be("BUSY", "ext 130 ends the call in Busy() without answering it");
    }

    /// <summary>Captures the first CdrEvent and signals a TaskCompletionSource.</summary>
    private sealed class CdrObserver(TaskCompletionSource<CdrEvent> tcs) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is CdrEvent cdr)
                tcs.TrySetResult(cdr);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Captures the first CdrEvent matching a predicate.</summary>
    private sealed class FilteredCdrObserver(Func<CdrEvent, bool> predicate, TaskCompletionSource<CdrEvent> tcs)
        : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is CdrEvent cdr && predicate(cdr))
                tcs.TrySetResult(cdr);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Routes each CdrEvent through a callback for classification.</summary>
    private sealed class ClassifyingCdrObserver(Action<CdrEvent> onCdr) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            if (value is CdrEvent cdr)
                onCdr(cdr);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
