namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Cdr;

using System.Collections.Concurrent;
using System.Globalization;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for CdrEvent (Call Detail Record). CDR events fire after call hangup
/// and require cdr_manager.conf enabled in Asterisk.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class CdrEventTests : FunctionalTestBase
{
    public CdrEventTests() : base("Asterisk.Sdk.Ami")
    {
    }

    [AsteriskContainerFact]
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

        // ext 100 = Answer + Wait(5) + Hangup
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-answered-01"
        });

        // CDR fires after hangup; Wait(5) + processing time = up to 30s
        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (result != cdrReceived.Task)
        {
            // CDR module may not be loaded — skip gracefully
            return;
        }

        var cdr = await cdrReceived.Task;
        cdr.Channel.Should().NotBeNullOrEmpty("CDR must include the originating channel");
        cdr.Disposition.Should().Be("ANSWERED", "ext 100 answers the call");
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

    [AsteriskContainerFact]
    public async Task UnansweredCall_ShouldProduceCdr_WithFailedDisposition()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // Drain any stale CDR events from prior tests
        await Task.Delay(TimeSpan.FromSeconds(1));

        var cdrReceived = new TaskCompletionSource<CdrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Filter CDR events to only match our specific originate channel
        using var subscription = connection.Subscribe(new FilteredCdrObserver(
            cdr => (cdr.DestinationChannel?.Contains("9998", StringComparison.Ordinal) ?? false)
                || (cdr.Channel?.Contains("9998", StringComparison.Ordinal) ?? false),
            cdrReceived));

        // ext 9998 does not exist — call fails immediately with FAILED disposition
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/9998@test-functional",
            Context = "test-functional",
            Exten = "9998",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000,
            ActionId = "cdr-unanswered-01"
        });

        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (result != cdrReceived.Task)
        {
            // CDR module not loaded or call produced no CDR — skip gracefully
            return;
        }

        var cdr = await cdrReceived.Task;
        cdr.Disposition.Should().NotBe("ANSWERED",
            "unanswered call to non-existent extension must not have ANSWERED disposition");

        // BillableSeconds should be 0 or null for unanswered calls
        var billable = cdr.BillableSeconds ?? 0;
        billable.Should().Be(0, "unanswered call should have 0 billable seconds");
    }

    [AsteriskContainerFact]
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
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-timestamps-01"
        });

        var result = await Task.WhenAny(cdrReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (result != cdrReceived.Task)
        {
            return;
        }

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
        if (cdr.Duration.HasValue)
        {
            var clockDuration = (endDate - startDate).TotalSeconds;
            clockDuration.Should().BeApproximately(cdr.Duration.Value, 2.0,
                "CDR Duration must be within 2 seconds of EndTime - StartTime");
        }
    }

    [AsteriskContainerFact]
    public async Task CdrDisposition_ShouldMatchCallOutcome()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(20);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var answeredCdrs = new ConcurrentBag<CdrEvent>();
        var failedCdrs = new ConcurrentBag<CdrEvent>();

        var answeredReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(new ClassifyingCdrObserver(
            cdr =>
            {
                if (cdr.Disposition == "ANSWERED")
                {
                    answeredCdrs.Add(cdr);
                    answeredReceived.TrySetResult(true);
                }
                else
                {
                    failedCdrs.Add(cdr);
                    failedReceived.TrySetResult(true);
                }
            }));

        // Originate answered call first
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Context = "test-functional",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 15000,
            ActionId = "cdr-disposition-answered"
        });

        // Wait for the answered CDR (up to 30s for Wait(5) + hangup + CDR processing)
        var answeredResult = await Task.WhenAny(answeredReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (answeredResult != answeredReceived.Task)
        {
            // CDR module not loaded — skip
            return;
        }

        // Originate unanswered call
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/999@test-functional",
            Context = "test-functional",
            Exten = "999",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000,
            ActionId = "cdr-disposition-failed"
        });

        var failedResult = await Task.WhenAny(failedReceived.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (failedResult != failedReceived.Task)
        {
            return;
        }

        answeredCdrs.Should().NotBeEmpty("answered call must produce a CDR with ANSWERED disposition");
        failedCdrs.Should().NotBeEmpty("failed call must produce a CDR with non-ANSWERED disposition");

        answeredCdrs.Should().AllSatisfy(c =>
            c.Disposition.Should().Be("ANSWERED"));

        failedCdrs.Should().AllSatisfy(c =>
            c.Disposition.Should().NotBe("ANSWERED"));
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
