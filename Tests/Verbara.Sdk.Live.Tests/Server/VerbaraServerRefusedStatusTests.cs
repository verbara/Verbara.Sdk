using System.Collections.Concurrent;
using System.Diagnostics;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// A load of live state whose <c>Status</c> request Asterisk refuses. An AMI user whose write classes allow none of
/// <c>system</c>, <c>call</c> or <c>reporting</c> may not run <c>Status</c>, and Asterisk answers it with
/// <c>Response: Error</c> and <c>Message: Permission denied</c>. The load read that refusal as a snapshot with no
/// channels and reconciled the channel table against it, which removed every held channel and ended every call while
/// Asterisk still held it: 2 → 0, 60 of 60 runs on Asterisk 20.20.1, 22.9.0 and 23.4.1.
/// </summary>
/// <remarks>
/// Each test runs a real <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> and a <see cref="VerbaraServer"/> through
/// <see cref="Run"/>. The two channels are the two legs of one call, as <c>Status</c> lists them. A load asks
/// <c>Status</c>, reconciles, then asks <c>QueueStatus</c> and <c>Agents</c>: a test that asserts what a reload left
/// in place first waits until the peer has read the reload's <c>Agents</c>, which proves the reload got past the
/// reconciliation, so the assertion cannot pass before the reload has run.
/// </remarks>
public sealed class VerbaraServerRefusedStatusTests
{
    private const string StatusRefused = "[LIVE] Status refused";
    private const string StatusRefusedTag = "live.status.refused";

    private static IReadOnlyList<StatusChannel> OneCallTwoLegs() =>
    [
        new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
        new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
    ];

    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepEveryHeldChannel_WhenAsteriskRefusesStatus()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var heldAtStart = run.Server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.StatusRefusedFromAsk = 2;
        var outcome = await Record.ExceptionAsync(
            () => run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound));

        using (new AssertionScope())
        {
            heldAtStart.Should().Be(2, "the start loaded both legs of the call");
            outcome.Should().BeNull("a refused Status does not fail the load");
            removed.Should().BeEmpty("a refusal is no evidence that a channel is gone");
            run.Server.Channels.ChannelCount.Should().Be(2, "both legs Asterisk still holds are still held");
            peer.Asked("Status").Should().Be(2, "the start's Status and the refused one");
            peer.Asked("QueueStatus").Should().Be(2, "the load goes on to the queues after the refusal");
            peer.Asked("Agents").Should().Be(2, "and to the agents");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task Reload_ShouldKeepEveryHeldChannel_WhenAsteriskRefusesStatusAfterAReconnect()
    {
        var first = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        var second = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs(), StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(first, second, autoReconnect: true);
        var heldAtStart = run.Server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        run.ReleaseSecondPeer();
        first.CloseSession();
        var reconnected = await CompletesWithinBoundAsync(run.Reconnected);
        // The barrier: the reload asks Agents only after it has read Status and reconciled.
        var pastTheReconcile = await CompletesWithinBoundAsync(second.AskedAtLeast("Agents", 1));

        using (new AssertionScope())
        {
            heldAtStart.Should().Be(2, "the start loaded both legs of the call");
            reconnected.Should().BeTrue("the connection reconnected to the second session");
            pastTheReconcile.Should().BeTrue("the reload got past its Status and its reconciliation");
            second.Asked("Status").Should().Be(1, "the reload asked Status once and Asterisk refused it");
            removed.Should().BeEmpty("a refusal after a reconnect is no evidence that a channel is gone");
            run.Server.Channels.ChannelCount.Should().Be(2, "both legs Asterisk still holds are still held");
            first.Fault.Should().BeNull("the first peer served its session without failing");
            second.Fault.Should().BeNull("the second peer served its session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldLogTheRefusalOnceAtWarning_WhenAsteriskRefusesStatus()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);

        peer.StatusRefusedFromAsk = 2;
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        var refusals = run.ServerLog.Entries
            .Where(e => e.Line.Contains(BootingAsterisk.PermissionDenied, StringComparison.Ordinal)).ToList();
        using (new AssertionScope())
        {
            refusals.Should().ContainSingle("the load says once that Asterisk refused its Status, with Asterisk's message");
            refusals.Should().OnlyContain(e => e.Level == LogLevel.Warning, "a channel table left unreconciled is a Warning");
            refusals.Should().OnlyContain(e => e.Line.StartsWith(StatusRefused, StringComparison.Ordinal),
                $"the line names what happened: {StatusRefused}");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task RequestInitialStateAsync_ShouldTagTheLoadsActivityWithTheRefusal_WhenAsteriskRefusesStatus()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);

        // The loads of this test run under a parent of its own, so a load another test runs at the same time is not
        // read as one of this test's.
        using var testSource = new ActivitySource("Verbara.Sdk.Live.Tests.RefusedStatus");
        var loads = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Verbara.Sdk.Live" or "Verbara.Sdk.Live.Tests.RefusedStatus",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => loads.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        string parentId;
        using (var parent = testSource.StartActivity("refused-status test"))
        {
            parentId = parent?.Id ?? throw new InvalidOperationException("The test's parent activity was not sampled.");
            peer.StatusRefusedFromAsk = 3;
            await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
            await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        }

        var mine = loads.Where(a => a.OperationName == "live state-load" && a.ParentId == parentId).ToList();
        using (new AssertionScope())
        {
            mine.Should().HaveCount(2, "the test ran two loads under its parent");
            mine.Should().ContainSingle(a => a.GetTagItem(StatusRefusedTag) == null,
                "the load whose Status Asterisk answered carries no refusal");
            mine.Should().ContainSingle(a => Equals(a.GetTagItem(StatusRefusedTag), BootingAsterisk.PermissionDenied),
                "the refused load's trace says why the channel table was left alone, with Asterisk's message");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    /// <summary>
    /// A read Asterisk refuses still ends, and its window closes with it: the departures recorded while the refused
    /// <c>Status</c> was pending are dropped, so a refusal leaves nothing behind that could grow.
    /// </summary>
    [Fact]
    public async Task RequestInitialStateAsync_ShouldKeepNoDeparture_WhenAsteriskRefusedTheStatusTheyWereRecordedFor()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.StatusRefusedFromAsk = 2;
        peer.StatusAnsweredAfter = answer.Task;
        try
        {
            var load = run.Server.RequestInitialStateAsync().AsTask();
            // The peer counts the Status before it answers, and the load opened its window before it sent it.
            var readOpen = await CompletesWithinBoundAsync(peer.AskedAtLeast("Status", 2));
            run.Server.Channels.OnHangup("1700000000.1");
            run.Server.Channels.OnHangup("1700000000.9");
            var recordedWhileOpen = ReadWindowRecord.Departures(run.Server.Channels);
            answer.SetResult();
            var outcome = await Record.ExceptionAsync(() => load.WaitAsync(Run.Bound));

            using (new AssertionScope())
            {
                readOpen.Should().BeTrue("the load asked Status a second time");
                recordedWhileOpen.Should().Be(2,
                    "a held leg and a channel never held hung up while the read was open, and both were recorded for it");
                outcome.Should().BeNull("a refused Status does not fail the load");
                ReadWindowRecord.Departures(run.Server.Channels).Should().Be(0,
                    "the refused read ended, and nothing it recorded outlives it");
                ReadWindowRecord.OpenWindows(run.Server.Channels).Should().Be(0, "the refused read's window is closed");
                peer.Fault.Should().BeNull("the peer served the session without failing");
            }
        }
        finally
        {
            answer.TrySetResult();
        }
    }

    /// <summary>The control: a <c>Status</c> Asterisk answers with an empty list still removes a channel it no longer has.</summary>
    [Fact]
    public async Task RequestInitialStateAsync_ShouldRemoveAGoneChannel_WhenStatusSucceedsWithNoChannels()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = OneCallTwoLegs() };
        await using var run = await Run.StartAsync(peer);
        var heldAtStart = run.Server.Channels.ChannelCount;
        var removed = new ConcurrentQueue<string>();
        run.Server.Channels.ChannelRemoved += channel => removed.Enqueue(channel.UniqueId);

        peer.StatusChannels = [];
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);

        using (new AssertionScope())
        {
            heldAtStart.Should().Be(2, "the start loaded both legs of the call");
            removed.Should().BeEquivalentTo(["1700000000.1", "1700000000.2"],
                "a completed Status that lists no channel is evidence that both are gone");
            run.Server.Channels.ChannelCount.Should().Be(0);
            run.ServerLog.Entries.Should().NotContain(e => e.Line.StartsWith(StatusRefused, StringComparison.Ordinal),
                "Asterisk refused nothing");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
