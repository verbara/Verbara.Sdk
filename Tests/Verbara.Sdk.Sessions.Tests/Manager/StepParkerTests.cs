using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The parker's own tests: it stops a real handler of the named server at its step and lets it go on demand, it
/// leaves other servers and other steps alone, every wait it offers is bounded, a missed let-go and a hook that throws
/// both end red, and the engine reaches each handler step of its seam on the path that step names. Each handler runs
/// on a thread the test starts, so the test thread is never the one parked; no sleep stands in for a signal.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class StepParkerTests : IAsyncLifetime
{
    /// <summary>Bound on the test's own waits for a handler thread; far above anything a passing run takes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly CallSessionManager _manager =
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());

    private readonly List<VerbaraServer> _servers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    private VerbaraServer Attach(string serverId)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.10.1");
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        _servers.Add(server);
        _manager.AttachToServer(server, serverId);
        return server;
    }

    private static Task ArriveOnAnotherThread(VerbaraServer server, string uniqueId) =>
        Task.Factory.StartNew(
            () => server.Channels.OnNewChannel(uniqueId, "PJSIP/trunk-" + uniqueId, ChannelState.Up,
                context: "from-trunk", linkedId: uniqueId),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public async Task StepParker_ShouldHoldTheHandlerAtItsStepUntilLetGo_WhenAHandlerOfTheNamedServerReachesIt()
    {
        var a = Attach("a");
        using var parker = StepParker.Install(_manager, HandlerStep.ArrivalPublished, "a", "1700000000.1");

        var arrival = ArriveOnAnotherThread(a, "1700000000.1");
        parker.WaitUntilParked(Bound);

        var whileParked = new
        {
            ArrivalReturned = arrival.IsCompleted,
            HeldById = _manager.GetByLinkedId("1700000000.1") is not null,
            IndexedByChannel = _manager.GetByChannelId("1700000000.1") is not null,
            OnAnotherThread = parker.ParkedThreadId != Environment.CurrentManagedThreadId,
        };

        parker.LetGo();
        await arrival.WaitAsync(Bound);

        whileParked.Should().BeEquivalentTo(
            new { ArrivalReturned = false, HeldById = true, IndexedByChannel = false, OnAnotherThread = true },
            "parked at ArrivalPublished the call is published by id and linkedid but not yet by channel id, and the "
            + "handler has not returned");
        _manager.GetByChannelId("1700000000.1").Should().NotBeNull("once let go the handler finishes the arrival");
        parker.Faults.Should().BeEmpty();
    }

    [Fact]
    public void StepParker_ShouldLetEveryOtherServerAndStepThrough_WhenTheyReachTheSeam()
    {
        var a = Attach("a");
        Attach("b");
        using var parker = StepParker.Install(_manager, HandlerStep.ArrivalPublished, "b", bound: Bound);
        using var otherStep = StepParker.Install(_manager, HandlerStep.RemovalFound, "a", bound: Bound);

        a.Channels.OnNewChannel("1700000000.2", "PJSIP/trunk-2", ChannelState.Up, context: "from-trunk",
            linkedId: "1700000000.2");

        new
        {
            Indexed = _manager.GetByChannelId("1700000000.2") is not null,
            NamedServerParked = parker.HasParked,
            OtherStepParked = otherStep.HasParked,
        }.Should().BeEquivalentTo(new { Indexed = true, NamedServerParked = false, OtherStepParked = false },
            "an arrival of server 'a' runs straight through a parker of server 'b' and one of another step, on the "
            + "test's own thread");
    }

    [Fact]
    public void StepParker_ShouldThrowTimeout_WhenNoHandlerReachesTheStepWithinTheBound()
    {
        Attach("a");
        using var parker = StepParker.Install(_manager, HandlerStep.RemovalFound, "a", bound: Bound);

        var wait = () => parker.WaitUntilParked(TimeSpan.FromMilliseconds(100));

        wait.Should().Throw<TimeoutException>().WithMessage("*server 'a'*RemovalFound*");
    }

    [Fact]
    public async Task StepParker_ShouldReleaseTheHandlerAndEndRed_WhenTheTestNeverLetsItGo()
    {
        var a = Attach("a");
        var parker = StepParker.Install(_manager, HandlerStep.ArrivalBuilt, "a", bound: TimeSpan.FromMilliseconds(200));

        var arrival = ArriveOnAnotherThread(a, "1700000000.3");
        parker.WaitUntilParked(Bound);
        await arrival.WaitAsync(Bound);

        var dispose = parker.Dispose;
        dispose.Should().Throw<TimeoutException>().WithMessage("*ArrivalBuilt*never let go*",
            "a missed let-go is released by the parker's own bound, so the lane does not hang, and is red");
        _manager.GetByChannelId("1700000000.3").Should().NotBeNull("the released handler completed its arrival");
    }

    [Fact]
    public void StepParker_ShouldBeSeenRed_WhenItsHookThrows()
    {
        var a = Attach("a");
        var throwing = new StepParker(HandlerStep.ArrivalBuilt, "a", bound: Bound)
        {
            WhenReached = () => throw new InvalidOperationException("hook fault"),
        };
        throwing.Install(_manager);

        Exception? escaped = null;
        try
        {
            a.Channels.OnNewChannel("1700000000.4", "PJSIP/trunk-4", ChannelState.Up, context: "from-trunk",
                linkedId: "1700000000.4");
        }
        catch (InvalidOperationException ex)
        {
            escaped = ex;
        }

        var dispose = throwing.Dispose;
        dispose.Should().Throw<InvalidOperationException>().WithMessage("*ArrivalBuilt*hook fault*",
            "a hook that throws inside the engine is recorded and surfaces when the parker is disposed");
        new { Escaped = escaped?.Message, Opened = _manager.GetByChannelId("1700000000.4") is not null }
            .Should().BeEquivalentTo(new { Escaped = "hook fault", Opened = false },
                "the hook's exception propagates out of the handler as an exception at that point would, so the "
                + "arrival it interrupted opened no call");
    }

    [Theory]
    [InlineData(nameof(HandlerStep.ArrivalBuilt), "1700000100.1")]
    [InlineData(nameof(HandlerStep.ArrivalHeldById), "1700000100.1")]
    [InlineData(nameof(HandlerStep.ArrivalPublished), "1700000100.1")]
    [InlineData(nameof(HandlerStep.JoinFound), "1700000100.2")]
    [InlineData(nameof(HandlerStep.QueueVisitWritten), "1700000100.1")]
    [InlineData(nameof(HandlerStep.AgentCompleteFound), "1700000100.1")]
    [InlineData(nameof(HandlerStep.RemovalFound), "1700000100.1")]
    public async Task StepHook_ShouldBeCalledWithTheServerIdAndTheLeg_WhenAQueueCallRunsThroughEveryHandler(
        string stepName, string leg)
    {
        var step = Enum.Parse<HandlerStep>(stepName);
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider(), null, "srv-1");
        var seen = new ConcurrentQueue<(HandlerStep Step, string ServerId, string Id)>();
        rig.Manager.StepHook = (s, serverId, id) => seen.Enqueue((s, serverId, id));

        // The caller 1700000100.1 rings, joins q1, member leg 1700000100.2 (same linkedid) answers and is bridged;
        // the member hangs up, app_queue reports AgentComplete for the caller, the caller hangs up.
        rig.Deliver(WrapUpFrames.QueueCallConnected("q1", "1700000100.1", "1700000100.2", "PJSIP/2001"));
        rig.Deliver(WrapUpFrames.MemberHangsUp("q1", "1700000100.1", "1700000100.2", "PJSIP/2001"));

        var hits = seen.Where(h => h.Step == step).ToList();
        hits.Should().Contain(h => h.Id == leg, $"the queue call's path runs through {step} for leg {leg}");
        hits.Should().OnlyContain(h => h.ServerId == "srv-1", "every handler names the server it was attached under");
    }
}
