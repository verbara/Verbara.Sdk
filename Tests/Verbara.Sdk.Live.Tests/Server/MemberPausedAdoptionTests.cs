using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Agents;
using Verbara.Sdk.Live.Server;
using Xunit.Abstractions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The bounded half of the order between a member's pause notification and its agent's state change: once another
/// thread's drain has waited out its turn behind a blocked handler, it raises every earlier notification no drain owns,
/// the member's pause included, before the thread that applied the pause reaches its own drain.
/// </summary>
public sealed class MemberPausedAdoptionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnMemberPaused_ShouldRaiseTheMemberPausedOnceOnTheAdoptingThread_WhenAnotherDrainClaimsIt()
    {
        var connection = Substitute.For<IAmiConnection>();
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => observer = obs)).Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(Empty());
        var logger = new CapturingLogger();
        await using var server = new VerbaraServer(connection, logger);
        await server.StartAsync();
        observer!.OnNext(new AgentLoginEvent { Agent = "1001", Channel = "PJSIP/1001" });
        observer.OnNext(new QueueMemberAddedEvent { Queue = "sales", Interface = "Agent/1001", Paused = false, Status = 1 });
        observer.OnNext(new QueueMemberAddedEvent { Queue = "sales", Interface = "PJSIP/block", Paused = false, Status = 1 });

        using var blockerEntered = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var pauseApplied = new ManualResetEventSlim();
        using var releasePoster = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var memberPausedThreads = new ConcurrentQueue<int>();
        var agentChanges = new ConcurrentQueue<(AgentState State, int Thread)>();
        server.Queues.MemberStatusChanged += (_, m) =>
        {
            if (m.Interface != "PJSIP/block")
                return;
            blockerEntered.Set();
            releaseBlocker.Wait(Bound);
        };
        server.Queues.QueueUpdated += _ => order.Enqueue("QueueUpdated on " + Environment.CurrentManagedThreadId);
        server.Queues.MemberPaused += (_, _) =>
        {
            memberPausedThreads.Enqueue(Environment.CurrentManagedThreadId);
            order.Enqueue("MemberPaused on " + Environment.CurrentManagedThreadId);
            throw new InvalidOperationException("member-paused handler");
        };
        server.Agents.AgentStateChanged += a =>
        {
            agentChanges.Enqueue((a.State, Environment.CurrentManagedThreadId));
            order.Enqueue("AgentStateChanged " + a.State + " on " + Environment.CurrentManagedThreadId);
        };
        var hooked = 0;
        server.Queues.MemberPauseApplied = () =>
        {
            if (Interlocked.Exchange(ref hooked, 1) != 0)
                return;
            pauseApplied.Set();
            releasePoster.Wait(Bound);
        };

        // A handler that blocks heads the outbox.
        var blocker = Start("blocker", () => server.Queues.OnMemberStatusChanged("sales", "PJSIP/block", 2));
        blockerEntered.Wait(Bound).Should().BeTrue("premise: the blocking handler runs");

        // The pause is applied and posted, and its thread is held before it drains.
        Exception? posterFailure = null;
        var poster = Start("poster", () =>
        {
            try
            {
                observer.OnNext(new QueueMemberPauseEvent
                {
                    Paused = true,
                    RawFields = new Dictionary<string, string> { ["Queue"] = "sales", ["Interface"] = "Agent/1001" },
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                posterFailure = ex;
            }
        });
        pauseApplied.Wait(Bound).Should().BeTrue("premise: the pause was applied to a held member");

        // A third thread's drain waits out its turn behind the blocked handler and claims the unowned pause.
        var adopter = Start("adopter", () => server.Queues.OnQueueParams("sales", 0, "ringall", 0, 0, 0, 0, 0));
        var adopterThread = adopter.ManagedThreadId;
        var adopterFinished = adopter.Join(Bound);
        releasePoster.Set();
        var posterFinished = poster.Join(Bound);
        releaseBlocker.Set();
        var blockerFinished = blocker.Join(Bound);

        output.WriteLine($"blocker={blocker.ManagedThreadId} poster={poster.ManagedThreadId} adopter={adopterThread}");
        foreach (var line in order)
            output.WriteLine(line);

        using (new AssertionScope())
        {
            (adopterFinished && posterFinished && blockerFinished).Should().BeTrue("every thread finishes");
            memberPausedThreads.Should().Equal([adopterThread], "the claimed notification is raised once, by the drain that claimed it");
            posterFailure.Should().BeNull("an adopted notification's handler failure does not reach the call that posted it");
            logger.Errors.Should().ContainSingle(e => e is InvalidOperationException && e.Message == "member-paused handler",
                "it reaches the unowned-failure report");
            agentChanges.Should().Equal([(AgentState.Paused, poster.ManagedThreadId)], "the agent is still derived on the posting thread");
            server.Agents.GetById("1001")!.State.Should().Be(AgentState.Paused);
        }
    }

    private static Thread Start(string name, Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }

    private static async IAsyncEnumerable<ManagerEvent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class CapturingLogger : ILogger<VerbaraServer>
    {
        private readonly ConcurrentQueue<Exception> _errors = new();

        public IReadOnlyList<Exception> Errors => [.. _errors];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
                _errors.Enqueue(exception);
        }
    }
}
