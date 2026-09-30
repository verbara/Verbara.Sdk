using System.Buffers;
using System.Text;
using Verbara.Sdk.Ami.Tests.Connection;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// The boot-window rig checked on its own, so a red boot-window test is the code's and never the rig's: the manual
/// clock moves its timestamps and fires its timers only when advanced, the peer's channels reach the server, a log
/// line can be awaited by its count, and what a connection sends after a close can be read.
/// </summary>
/// <remarks>Nothing here waits on the wall clock: the clock moves only on <see cref="FakeTimeProvider.Advance"/>.</remarks>
public sealed class RigSelfTests
{
    [Fact]
    public void GetElapsedTime_ShouldReportExactlyTenSeconds_WhenTheClockIsAdvancedTenSeconds()
    {
        var clock = new FakeTimeProvider();
        var start = clock.GetTimestamp();
        var utcStart = clock.GetUtcNow();

        var beforeAdvance = clock.GetElapsedTime(start);
        clock.Advance(TimeSpan.FromSeconds(10));

        using (new AssertionScope())
        {
            beforeAdvance.Should().Be(TimeSpan.Zero, "the timestamp moves only when the clock is advanced");
            clock.GetElapsedTime(start).Should().Be(TimeSpan.FromSeconds(10),
                "the timestamp is the manual time, so an elapsed time reads exactly what was advanced");
            (clock.GetUtcNow() - utcStart).Should().Be(TimeSpan.FromSeconds(10), "the wall time moves with it");
        }
    }

    [Fact]
    public async Task Advance_ShouldEndATwoHundredMillisecondWaitOnTheFake_WhenTheClockReachesItsDueTimeAndNotBefore()
    {
        // A wait timed by a TimeProvider that is not the system one runs on the provider's one-shot timer, created
        // with the wait's due time and no period: that is also how Task.Delay(TimeSpan, TimeProvider,
        // CancellationToken) waits on it.
        var clock = new FakeTimeProvider();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = neverCompletes.Task.WaitAsync(TimeSpan.FromMilliseconds(200), clock);
        var created = clock.TimersCreated.TryRead(out var timer);
        clock.Advance(TimeSpan.FromMilliseconds(199));
        var completedEarly = wait.IsCompleted;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await SettleWithinBoundAsync(wait);

        using (new AssertionScope())
        {
            created.Should().BeTrue("the wait creates its timer on the fake, where a test can read it before advancing");
            timer!.DueTime.Should().Be(TimeSpan.FromMilliseconds(200), "the timer carries the delay the wait asked for");
            completedEarly.Should().BeFalse("199 ms is short of the 200 ms the wait asked for");
            wait.IsFaulted.Should().BeTrue("the fake fired the timer when the clock reached 200 ms");
            wait.Exception?.InnerException.Should().BeOfType<TimeoutException>("the wait ended because its time ran out");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldLoadTheChannelsThePeerLists_WhenThePeerAnswersStatus()
    {
        var peer = new BootingAsterisk
        {
            BootedAtLogin = true,
            StatusChannels =
            [
                new StatusChannel("1700000000.1", "PJSIP/1001-00000001", LinkedId: "1700000000.1"),
                new StatusChannel("1700000000.2", "PJSIP/1002-00000002", LinkedId: "1700000000.1"),
            ],
        };

        await using var run = await Run.StartAsync(peer);

        var second = run.Server.Channels.GetByUniqueId("1700000000.2");
        using (new AssertionScope())
        {
            run.Server.Channels.ChannelCount.Should().Be(2, "the peer lists two channels");
            second.Should().NotBeNull("each listed channel is loaded under its Uniqueid");
            second!.Name.Should().Be("PJSIP/1002-00000002", "the channel keeps its name");
            second!.LinkedId.Should().Be("1700000000.1", "the channel keeps the Linkedid the peer sent");
            peer.Fault.Should().BeNull("the peer served the session without failing");
        }
    }

    [Fact]
    public async Task Logged_ShouldCompleteOnTheSecondMatchingLine_WhenTwoLinesAreAwaited()
    {
        var log = new SignalingLogger<RigSelfTests>();
        Write(log, LogLevel.Information, "[LIVE] State loaded: channels=0 queues=1 agents=1");

        var second = log.Logged("[LIVE] State loaded", times: 2);
        var pendingAfterOne = second.IsCompleted;
        Write(log, LogLevel.Warning, "[LIVE] State loaded: channels=0 queues=1 agents=1");
        var completed = await CompletesWithinBoundAsync(second);

        using (new AssertionScope())
        {
            pendingAfterOne.Should().BeFalse("one matching line is not two");
            completed.Should().BeTrue("the second matching line completes the wait");
            log.Entries.Select(entry => entry.Level).Should().Equal(
                [LogLevel.Information, LogLevel.Warning], "each line is kept with its level");
        }
    }

    [Fact]
    public async Task TakeUnreadActions_ShouldReturnTheActionsWrittenAfterThePeerClosed_WhenThePeerHasStoppedReading()
    {
        var socket = new PipedSocket();
        await WriteAsConnectionAsync(socket, "Action: Status\r\nActionID: 1\r\n\r\n");
        var read = await socket.ReadActionAsync(CancellationToken.None);

        socket.CloseFromPeer();
        var readAfterClose = await socket.ReadActionAsync(CancellationToken.None);
        await WriteAsConnectionAsync(socket, "Action: QueueStatus\r\nActionID: 2\r\n\r\n");
        var unread = socket.TakeUnreadActions();

        using (new AssertionScope())
        {
            read.Should().StartWith("Action: Status\r\n", "the peer reads what the connection sends while the session lives");
            readAfterClose.Should().BeNull("a peer that closed the session reads nothing more");
            unread.Should().ContainSingle("only the action sent after the close is left unread")
                .Which.Should().Be("Action: QueueStatus\r\nActionID: 2\r\n\r\n", "it is returned whole");
            socket.TakeUnreadActions().Should().BeEmpty("what was taken is not taken twice");
        }
    }

    /// <summary>Writes one line at <paramref name="level"/>, as a generated logger method does.</summary>
    private static void Write(SignalingLogger<RigSelfTests> logger, LogLevel level, string line) =>
        logger.Log(level, default, line, exception: null, static (state, _) => state);

    private static async Task WriteAsConnectionAsync(PipedSocket socket, string action)
    {
        socket.Output.Write(Encoding.UTF8.GetBytes(action));
        await socket.Output.FlushAsync();
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
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return false;
        }
    }

    /// <summary>Awaits <paramref name="task"/> until it ends or the hang bound passes; the caller asserts on its state.</summary>
    private static async Task SettleWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Run.Bound);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // How the task ended, or that it never did, is read from the task itself by the assertions.
        }
    }
}
