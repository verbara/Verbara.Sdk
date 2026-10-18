using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// How long a queue member's wrap-up lasts when the host registers an <see cref="IWrapUpDurationProvider"/>: which
/// server and queue it is asked about, when, the fallback to <see cref="SessionOptions.WrapUpDuration"/>, and what the
/// manager does with an answer it cannot use or a provider that misbehaves. On the real manager, with no agent tracker
/// resolved, driven through <see cref="WrapUpRig"/>'s real <c>VerbaraServer</c> switch.
/// </summary>
/// <remarks>
/// <para>
/// Frames are shaped like the committed recordings, where every <c>AgentComplete</c> carries a <c>Queue</c> header.
/// A case whose shape no recording holds (the header differs from the call's queue, a missed join, a
/// <c>linkedid</c> two servers report, a frame with no <c>Queue</c> header, 1 025 queues) builds its frames here and
/// says so in its name: it pins the SDK's rule, not Asterisk's behaviour.
/// </para>
/// <para>
/// A provider log is recognised by its event name, which contains the name of its kind: <c>WrapUpDurationRejected</c>,
/// <c>WrapUpDurationProviderFailed</c>, <c>WrapUpDurationProviderSlow</c>, <c>WrapUpDurationExceedsRetention</c> and
/// <c>WrapUpDurationWarnSetFull</c>. A log names the server and the queue when they are among its structured values.
/// </para>
/// <para>
/// Every case but the lock cases runs on <see cref="FakeTimeProvider"/>, whose timers fire on the test's thread inside
/// <see cref="FakeTimeProvider.Advance"/>. The lock cases run on <see cref="ThreadedTimeProvider"/>. No test sleeps.
/// </para>
/// </remarks>
public sealed class CallSessionManagerQueueWrapUpDurationTests
{
    private const string Queue = "sales";
    private const string Member = "PJSIP/agent1";
    private const string Rejected = "WrapUpDurationRejected";
    private const string Failed = "WrapUpDurationProviderFailed";
    private const string Slow = "WrapUpDurationProviderSlow";
    private const string ExceedsRetention = "WrapUpDurationExceedsRetention";
    private const string WarnSetFull = "WrapUpDurationWarnSetFull";
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);
    private static readonly string[] Kinds = [Rejected, Failed, Slow, ExceedsRetention, WarnSetFull];
    private static readonly string[] RejectedThenFailed = [Rejected, Failed];
    private static readonly LogLevel[] WarningThenTwoDebug = [LogLevel.Warning, LogLevel.Debug, LogLevel.Debug];
    private static readonly LogLevel[] OneDebug = [LogLevel.Debug];
    private static readonly string[] WrapUpBeforeConnect = ["CallWrapUpEvent:first", "CallConnectedEvent:second"];

    private static SessionOptions Options(TimeSpan? wrapUp = null) => new() { WrapUpDuration = wrapUp ?? WrapUp };

    // --- Reds: the seam stores the provider and never asks it ----------------------------------------------------

    [Fact]
    public async Task AgentComplete_ShouldCloseEachWrapUpAfterItsQueuesDuration_WhenTheProviderAnswersPerQueue()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, queue) => queue switch
        {
            "sales" => TimeSpan.FromSeconds(15),
            "support" => TimeSpan.FromSeconds(45),
            _ => null,
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected("sales", "c1", "m1", "PJSIP/a1"));
        rig.Deliver(QueueCallConnected("support", "c2", "m2", "PJSIP/a2"));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp("sales", "c1", "m1", "PJSIP/a1"));
        rig.Deliver(MemberHangsUp("support", "c2", "m2", "PJSIP/a2"));
        var first = rig.Session("c1").SessionId;
        var second = rig.Session("c2").SessionId;

        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = new[] { (ServerId, "sales"), (ServerId, "support") },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(first, ServerId, completedAt + TimeSpan.FromSeconds(15), null, "sales",
                        TimeSpan.FromSeconds(15)),
                    new CallWrapUpEvent(second, ServerId, completedAt + TimeSpan.FromSeconds(45), null, "support",
                        TimeSpan.FromSeconds(45)),
                },
            },
            options => options.WithStrictOrdering(),
            "the provider is asked once per AgentComplete with the server and the queue it names, and each wrap-up "
            + "closes after its own queue's duration (15 s and 45 s), not the global 30 s. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldUseEachServersDuration_WhenTheSameQueueNameRunsOnTwoServers()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, server, _) => server switch
        {
            "s1" => TimeSpan.FromSeconds(10),
            "s2" => TimeSpan.FromSeconds(20),
            _ => null,
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null, "s1", "s2");
        rig.DeliverTo("s1", QueueCallConnected(Queue, "s1c1", "s1m1", Member));
        rig.DeliverTo("s2", QueueCallConnected(Queue, "s2c1", "s2m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.DeliverTo("s1", MemberHangsUp(Queue, "s1c1", "s1m1", Member));
        rig.DeliverTo("s2", MemberHangsUp(Queue, "s2c1", "s2m1", Member));
        var onS1 = rig.Session("s1c1").SessionId;
        var onS2 = rig.Session("s2c1").SessionId;

        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = new[] { ("s1", Queue), ("s2", Queue) },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(onS1, "s1", completedAt + TimeSpan.FromSeconds(10), null, Queue,
                        TimeSpan.FromSeconds(10)),
                    new CallWrapUpEvent(onS2, "s2", completedAt + TimeSpan.FromSeconds(20), null, Queue,
                        TimeSpan.FromSeconds(20)),
                },
            },
            options => options.WithStrictOrdering(),
            "one queue name on two servers is two queues: the provider is asked with each server's id, and the "
            + "wrap-up on s1 closes after 10 s, the one on s2 after 20 s. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskWithTheServerThatDeliveredIt_WhenTheCallsLinkedIdIsAlsoReportedByAnotherServer_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null, "s1", "s2");
        rig.DeliverTo("s1", QueueCallConnected(Queue, "c1", "m1", Member));
        // Synthetic: s2 reports a caller leg carrying s1's linkedid, then the member's AgentComplete for it.
        rig.DeliverTo("s2", NewChannel("s2c1", CallerChannel("s2c1"), "Up", "c1", context: "from-trunk", exten: "500"));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.DeliverTo("s2", AgentComplete(Queue, "s2c1", CallerChannel("s2c1"), "c1", Member, "s2m1",
            MemberChannel(Member, "s2m1"), 60));
        var callServer = rig.Session("c1").ServerId;

        new { CallServer = callServer, Asks = provider.Asks }.Should().BeEquivalentTo(
            new { CallServer = "s1", Asks = new[] { ("s2", Queue) } },
            "the call is s1's, but the AgentComplete came through s2's attachment: the provider is asked with s2, "
            + "the server that reported the member finishing, not with the call's server. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskForTheSecondQueueOnly_WhenTheCallLeftTheFirstQueueUnansweredAndWasAnsweredInTheSecond()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        var callerChannel = CallerChannel("c1");
        var memberChannel = MemberChannel(Member, "m1");
        rig.Deliver(
            NewChannel("c1", callerChannel, "Ring", "c1", context: "from-trunk", exten: "500"),
            Join("q1", callerChannel, "c1", "c1"),
            Leave("q1", callerChannel, "c1", "c1"),
            Join("q2", callerChannel, "c1", "c1"),
            NewChannel("m1", memberChannel, "Down", "c1", context: "agents"),
            NewState("m1", "Up"),
            Leave("q2", callerChannel, "c1", "c1"),
            AgentConnect("q2", "c1", callerChannel, "c1", Member, "m1", memberChannel),
            NewState("c1", "Up"),
            BridgeCreate(Bridge("c1")),
            BridgeEnter(Bridge("c1"), "m1"),
            BridgeEnter(Bridge("c1"), "c1"));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp("q2", "c1", "m1", Member));

        provider.Asks.Should().Equal([(ServerId, "q2")],
            "the call crossed q1 unanswered and was answered in q2, whose member's AgentComplete names q2: the "
            + "provider is asked for q2 and never for q1. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskForAndReportTheQueueItNames_WhenTheCallerJoinedAnotherQueueBeforeIt_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, queue) => queue == "q1" ? TimeSpan.FromSeconds(20) : null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected("q1", "c1", "m1", Member));
        // Synthetic: the caller's join of q2 is handled before the q1 member's AgentComplete.
        rig.Deliver(Join("q2", CallerChannel("c1"), "c1", "c1"));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp("q1", "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = new[] { (ServerId, "q1") },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, completedAt + TimeSpan.FromSeconds(20), null, "q1",
                        TimeSpan.FromSeconds(20)),
                },
            },
            "the AgentComplete names q1, the queue whose member finished, while the call last joined q2: the provider "
            + "is asked for q1, the wrap-up runs q1's 20 s, and its CallWrapUpEvent carries q1. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskForTheQueueTheCallLastJoined_WhenTheFrameCarriesNoQueueHeader_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(
            BridgeLeave(Bridge("c1"), "m1"),
            Hangup("m1"),
            AgentCompleteWithoutQueueHeader("c1", "m1", Member),
            BridgeLeave(Bridge("c1"), "c1"),
            Hangup("c1"));

        provider.Asks.Should().Equal([(ServerId, Queue)],
            "an AgentComplete with no Queue header falls back to the queue the call last joined. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskForAndReportTheQueueTheCallLastJoined_WhenTheFramesQueueHeaderIsEmpty_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, queue) => queue == Queue ? TimeSpan.FromSeconds(12) : null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(
            BridgeLeave(Bridge("c1"), "m1"),
            Hangup("m1"),
            AgentCompleteWithQueueHeader("c1", "m1", Member, queueHeader: ""),
            BridgeLeave(Bridge("c1"), "c1"),
            Hangup("c1"));
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = new[] { (ServerId, Queue) },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, completedAt + TimeSpan.FromSeconds(12), null, Queue,
                        TimeSpan.FromSeconds(12)),
                },
            },
            options => options.WithStrictOrdering(),
            "an AgentComplete whose Queue header is present but empty names no queue: the provider is asked for, and "
            + "the CallWrapUpEvent carries, the queue the call last joined, never an empty name. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldAskForTheQueueItNames_WhenTheManagerNeverSawTheCallerJoinAQueue_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        var callerChannel = CallerChannel("c1");
        var memberChannel = MemberChannel(Member, "m1");
        rig.Deliver(AgentLogin("agent-1", Member, "login-1"));
        // Synthetic: app_queue's connect names the agent and q, but the caller's QueueCallerJoin was never seen.
        rig.Deliver(
            NewChannel("c1", callerChannel, "Ring", "c1", context: "from-trunk", exten: "500"),
            NewChannel("m1", memberChannel, "Down", "c1", context: "agents"),
            NewState("m1", "Up"),
            AgentConnect(Queue, "c1", callerChannel, "c1", Member, "m1", memberChannel, "agent-1"),
            NewState("c1", "Up"),
            BridgeCreate(Bridge("c1")),
            BridgeEnter(Bridge("c1"), "m1"),
            BridgeEnter(Bridge("c1"), "c1"));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member, agent: "agent-1"));
        clock.Advance(TimeSpan.FromMinutes(5));

        provider.Asks.Should().Equal([(ServerId, Queue)],
            "a call whose join the manager missed still has the member's AgentComplete, naming q: the provider is "
            + "asked for that queue, exactly once (the call's end asks nothing). " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenTheMembersWrapUpAndAskForTheQueueItNames_WhenTheManagerNeverSawTheCallerJoinAndTheMemberIsNotAnAgent_Synthetic()
    {
        const string member = "PJSIP/m";
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromSeconds(20));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);

        // Synthetic: app_queue's connect names q and a member with no Agent header, but the caller's QueueCallerJoin
        // was never seen, so the connect records nothing. First call: its wrap-up runs the provider's 20 s.
        rig.Deliver(MissedJoinConnected("c1", "m1", member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var firstCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", member));
        var first = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(1));

        // Second call, the same way; 5 s after its AgentComplete the member's next (seen) queue call connects, which
        // closes the wrap-up keyed by that interface on this server.
        rig.Deliver(MissedJoinConnected("c2", "m2", member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var secondCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c2", "m2", member));
        var second = rig.Session("c2").SessionId;
        clock.Advance(TimeSpan.FromSeconds(5));
        rig.Deliver(QueueCallConnected(Queue, "c3", "m3", member));
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = new[] { (ServerId, Queue), (ServerId, Queue) },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(first, ServerId, firstCompletedAt + TimeSpan.FromSeconds(20), null, Queue,
                        TimeSpan.FromSeconds(20)),
                    new CallWrapUpEvent(second, ServerId, secondCompletedAt + TimeSpan.FromSeconds(5), null, Queue,
                        TimeSpan.FromSeconds(5)),
                },
            },
            options => options.WithStrictOrdering(),
            "a member not known by name whose caller's join the manager missed still has its AgentComplete, naming q: "
            + "that AgentComplete opens the member's wrap-up, keyed by its interface on this server (so its next "
            + "connect there cuts it short), asks the provider once for q, runs the answer, and its CallWrapUpEvent "
            + "carries QueueName q and no agent. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenTheOnlyWrapUpOfTheLeg_WhenTheManagerNeverSawTheCallerJoinAndTheAgentIsKnownByName_Synthetic()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), wrapUpDurations: null, logger: null);
        rig.Deliver(AgentLogin("agent-1", Member, "login-1"));
        // Synthetic: app_queue's connect names the agent and q, but the caller's QueueCallerJoin was never seen.
        rig.Deliver(MissedJoinConnected("c1", "m1", Member, agent: "agent-1"));
        clock.Advance(TimeSpan.FromSeconds(60));
        var hangup = MemberHangsUp(Queue, "c1", "m1", Member, agent: "agent-1");
        rig.Deliver(hangup[0], hangup[1], hangup[2]);
        var completedAt = clock.GetUtcNow();
        // The caller hangs up 5 s after the AgentComplete, so the two open instants differ.
        clock.Advance(TimeSpan.FromSeconds(5));
        var endedAt = clock.GetUtcNow();
        rig.Deliver(hangup[3], hangup[4]);
        var sessionId = rig.Session("c1").SessionId;
        var callEnded = rig.Events.OfType<CallEndedEvent>().Count(e => e.SessionId == sessionId);
        clock.Advance(TimeSpan.FromMinutes(5));
        var wrapUps = rig.WrapUps.Where(w => w.SessionId == sessionId).ToArray();

        new
        {
            CallEnded = callEnded,
            Count = wrapUps.Length,
            OpenedAt = wrapUps.Select(w => w.Timestamp - w.WrapUpDuration).ToArray(),
            WrapUps = wrapUps,
        }.Should().BeEquivalentTo(
            new
            {
                CallEnded = 1,
                Count = 1,
                OpenedAt = new[] { completedAt },
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, "agent-1", Queue, WrapUp) },
            },
            options => options.WithStrictOrdering(),
            "an agent known by name whose caller's join the manager missed gets exactly one wrap-up for that leg: its "
            + "AgentComplete naming q opens it, 5 s before the call's CallEndedEvent (" + endedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            + "), and the call's end opens none. " + Describe(rig, provider: null));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenNoSecondWrapUpNorAskTheProvider_WhenTheManagerNeverSawTheCallerJoinAndTheCallEndedBeforeIt_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromSeconds(20));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(AgentLogin("agent-1", Member, "login-1"));
        // Synthetic: app_queue's connect names the agent and q, but the caller's QueueCallerJoin was never seen; the
        // call's end reaches the manager before the member's AgentComplete, which is then found by its Linkedid only.
        rig.Deliver(MissedJoinConnected("c1", "m1", Member, agent: "agent-1"));
        clock.Advance(TimeSpan.FromSeconds(60));
        var hangup = MemberHangsUp(Queue, "c1", "m1", Member, agent: "agent-1");
        var endedAt = clock.GetUtcNow();
        rig.Deliver(hangup[0], hangup[1], hangup[3], hangup[4]);
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromSeconds(1));
        rig.Deliver(hangup[2]);
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = Array.Empty<(string, string)>(),
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, endedAt + WrapUp, "agent-1", null, WrapUp) },
            },
            options => options.WithStrictOrdering(),
            "the call's end opened the agent's only wrap-up for that leg (WrapUpDuration, no provider asked, the call's "
            + "queue, which the missed join left empty) and took the leg off the call; an AgentComplete for an ended call does nothing: no second wrap-up under the member's "
            + "key, and no provider call. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentConnect_ShouldCutAPerQueueWrapUpShortBeforeAnnouncingTheCall_WhenTheMembersNextCallConnectsDuringIt()
    {
        // The global duration is shorter than the cut, so only the provider's 60 s keeps the wrap-up open until it.
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromSeconds(60));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(TimeSpan.FromSeconds(10)), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var first = rig.Session("c1").SessionId;

        clock.Advance(TimeSpan.FromSeconds(12));
        var before = rig.Events.Count;
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member));
        var second = rig.Session("c2").SessionId;
        var atConnect = rig.Events.Skip(before)
            .Where(e => e is CallWrapUpEvent or CallConnectedEvent)
            .Select(e => $"{e.GetType().Name}:{(e.SessionId == first ? "first" : e.SessionId == second ? "second" : "?")}")
            .ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtConnect = atConnect, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                AtConnect = WrapUpBeforeConnect,
                WrapUps = new[]
                {
                    new CallWrapUpEvent(first, ServerId, completedAt + TimeSpan.FromSeconds(12), null, Queue,
                        TimeSpan.FromSeconds(12)),
                },
            },
            options => options.WithStrictOrdering(),
            "a 60 s per-queue wrap-up is still open 12 s in, when the member's next call connects: that connect "
            + "closes it with 12 s measured, before the call's CallConnectedEvent, and nothing is raised at 60 s. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldRaiseAZeroLengthWrapUpAtOnceWithNoTimer_WhenTheProviderAnswersZero()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.Zero);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        var atComplete = rig.WrapUps.ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtComplete = atComplete, After = rig.WrapUps.Count, Timers = clock.TimersCreated.Count }
            .Should().BeEquivalentTo(
                new
                {
                    AtComplete = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt, null, Queue, TimeSpan.Zero) },
                    After = 1,
                    Timers = 0,
                },
                "a provider answer of zero closes that queue's wrap-up as it opens: a zero-length event while the "
                + "AgentComplete is handled, and no timer. " + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldFallBackToWrapUpDurationAndWarn_WhenTheProviderAnswersInfinite()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) => Timeout.InfiniteTimeSpan);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        new { WrapUps = rig.WrapUps.ToArray(), Rejected = logger.Of(Rejected) }.Should().BeEquivalentTo(
            new
            {
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp) },
                Rejected = new[] { new LogSummary(LogLevel.Warning, NamesServer: true, NamesQueue: true) },
            },
            "Timeout.InfiniteTimeSpan is a negative duration: the wrap-up falls back to the global 30 s, and one "
            + "Warning names the server and the queue. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldFallBackToWrapUpDurationWithoutThrowingAndWarn_WhenTheProviderAnswersMoreThanTheTimerAccepts()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromDays(50));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        Exception? escaped = null;
        try
        {
            rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        var sessionId = rig.Session("c1").SessionId;

        new
        {
            Escaped = escaped?.ToString(),
            WrapUps = rig.WrapUps.ToArray(),
            Rejected = logger.Of(Rejected),
        }.Should().BeEquivalentTo(
            new
            {
                Escaped = default(string),
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp) },
                Rejected = new[] { new LogSummary(LogLevel.Warning, NamesServer: true, NamesQueue: true) },
            },
            "50 days is above the 4 294 967 294 ms the system timer accepts: nothing is thrown, the wrap-up falls back "
            + "to the global 30 s, and one Warning names the server and the queue. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldFallBackEachTimeAndWarnOnceThenDebug_WhenTheProviderThrows()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) => throw new InvalidOperationException("provider down"));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        var expected = new List<CallWrapUpEvent>();
        var ended = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var caller = string.Create(CultureInfo.InvariantCulture, $"c{i}");
            var memberLeg = string.Create(CultureInfo.InvariantCulture, $"m{i}");
            rig.Deliver(QueueCallConnected(Queue, caller, memberLeg, Member));
            clock.Advance(TimeSpan.FromSeconds(60));
            var completedAt = clock.GetUtcNow();
            rig.Deliver(MemberHangsUp(Queue, caller, memberLeg, Member));
            var sessionId = rig.Session(caller).SessionId;
            expected.Add(new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp));
            ended.Add(sessionId);
            clock.Advance(WrapUp);
        }

        new
        {
            WrapUps = rig.WrapUps.ToArray(),
            Ended = rig.Events.OfType<CallEndedEvent>().Select(e => e.SessionId).ToArray(),
            Failed = logger.Of(Failed).Select(l => l.Level).ToArray(),
            Named = logger.Of(Failed).All(l => l.NamesServer && l.NamesQueue),
        }.Should().BeEquivalentTo(
            new { WrapUps = expected.ToArray(), Ended = ended.ToArray(), Failed = WarningThenTwoDebug, Named = true },
            options => options.WithStrictOrdering(),
            "a provider that throws never stops a wrap-up: each of the three opens and closes after the global 30 s, "
            + "the frames after each AgentComplete are still handled (each call ends), and the failure is logged at "
            + "Warning the first time and at Debug after, each naming the server and the queue. "
            + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldWarnForEachKind_WhenOneQueuesProviderFirstAnswersNegativeAndThenThrows()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((call, _, _) => call == 0
            ? TimeSpan.FromSeconds(-1)
            : throw new InvalidOperationException("provider down"));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        foreach (var (caller, memberLeg) in new[] { ("c1", "m1"), ("c2", "m2") })
        {
            rig.Deliver(QueueCallConnected(Queue, caller, memberLeg, Member));
            clock.Advance(TimeSpan.FromSeconds(60));
            rig.Deliver(MemberHangsUp(Queue, caller, memberLeg, Member));
            clock.Advance(WrapUp);
        }

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning && KindOf(e) is not null).ToArray();

        new
        {
            Kinds = warnings.Select(KindOf).ToArray(),
            Named = warnings.All(e => e.Names(ServerId) && e.Names(Queue)),
        }.Should().BeEquivalentTo(
            new { Kinds = RejectedThenFailed, Named = true },
            options => options.WithStrictOrdering(),
            "the kind is part of what is reported once: a rejected answer and a later failure for the same server and "
            + "queue are two Warnings, each naming them. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldUseAnAnswerAboveTheRetentionAndWarnNamingIt_WhenTheProviderAnswersLongerThanCompletedRetention()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var retention = TimeSpan.FromMinutes(5);
        var answer = TimeSpan.FromMinutes(6);
        var provider = new RecordingProvider((_, _, _) => answer);
        await using var rig = await WrapUpRig.StartAsync(clock,
            new SessionOptions { WrapUpDuration = WrapUp, CompletedRetention = retention }, provider, logger);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;

        clock.Advance(answer - OneMs);
        var justBefore = rig.WrapUps.Count;
        clock.Advance(OneMs);
        var atDue = rig.WrapUps.ToArray();
        var exceeds = logger.Entries.Where(e => KindOf(e) == ExceedsRetention).ToArray();

        new
        {
            JustBefore = justBefore,
            AtDue = atDue,
            Exceeds = exceeds.Select(e => new { e.Level, Server = e.Names(ServerId), Queue = e.Names(Queue), Retention = e.Names(retention) }).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                JustBefore = 0,
                AtDue = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + answer, null, Queue, answer) },
                Exceeds = new[] { new { Level = LogLevel.Warning, Server = true, Queue = true, Retention = true } },
            },
            "an answer of 6 min with a 5 min CompletedRetention is used unchanged, so the wrap-up closes after 6 min, "
            + "and one Warning names the server, the queue and the retention. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenTheWrapUpAtTheInstantItWasHandled_WhenTheProviderTakesTwoSecondsToAnswer()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return TimeSpan.FromSeconds(10);
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;

        clock.Advance(completedAt + TimeSpan.FromSeconds(10) - OneMs - clock.GetUtcNow());
        var justBefore = rig.WrapUps.Count;
        clock.Advance(OneMs);
        var atDue = rig.WrapUps.ToArray();

        new { JustBefore = justBefore, AtDue = atDue }.Should().BeEquivalentTo(
            new
            {
                JustBefore = 0,
                AtDue = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, completedAt + TimeSpan.FromSeconds(10), null, Queue,
                        TimeSpan.FromSeconds(10)),
                },
            },
            "the open instant is read before the provider is asked: a provider that takes 2 s to answer 10 s does not "
            + "move it, so the wrap-up closes 10 s after the AgentComplete was handled, measuring 10 s. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldCloseAtTheDueInstantWithTheAnswer_WhenTheProviderTakesLongerThanItsOwnAnswer()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return TimeSpan.FromSeconds(1);
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        var timersBefore = clock.TimersCreated.Count;
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        var atComplete = rig.WrapUps.ToArray();
        var timersAfter = clock.TimersCreated.Count;
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            AtComplete = atComplete,
            After = rig.WrapUps.Count,
            NewTimers = timersAfter - timersBefore,
            Slow = logger.Entries.Where(e => KindOf(e) == Slow).Select(e => e.Level).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                AtComplete = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, completedAt + TimeSpan.FromSeconds(1), null, Queue,
                        TimeSpan.FromSeconds(1)),
                },
                After = 1,
                NewTimers = 0,
                Slow = new[] { LogLevel.Warning },
            },
            "a provider that takes 2 s to answer 1 s answers after its own due instant: the wrap-up closes while the "
            + "AgentComplete is handled, with no timer, stamped at the due instant (open + 1 s) and measuring the "
            + "answer, 1 s, not the 2 s that passed; one Warning reports the slow provider. "
            + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldWarnNamingTheServerAndQueue_WhenTheProviderTakesTwoSecondsToAnswer()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return null;
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger, "s1");
        rig.DeliverTo("s1", QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.DeliverTo("s1", MemberHangsUp(Queue, "c1", "m1", Member));

        logger.Entries.Where(e => KindOf(e) == Slow)
            .Select(e => new { e.Level, Server = e.Names("s1"), Queue = e.Names(Queue) })
            .Should().BeEquivalentTo(
                new[] { new { Level = LogLevel.Warning, Server = true, Queue = true } },
                "a provider that takes 2 s to answer is above the 1 s bound: one Warning names s1 and the queue as "
                + "slow to answer. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task AgentComplete_ShouldStopRecordingAndSaySoOnce_WhenTheProviderFailsFor1025DistinctQueues_Synthetic()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) => throw new InvalidOperationException("provider down"));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        const int queues = 1025;
        for (var i = 1; i <= queues; i++)
        {
            var queue = QueueNumber(i);
            var caller = string.Create(CultureInfo.InvariantCulture, $"c{i}");
            var memberLeg = string.Create(CultureInfo.InvariantCulture, $"m{i}");
            rig.Deliver(QueueCallConnected(queue, caller, memberLeg, Member));
            rig.Deliver(MemberHangsUp(queue, caller, memberLeg, Member));
        }

        var failed = logger.Entries.Where(e => KindOf(e) == Failed).ToArray();

        new
        {
            FailedWarnings = failed.Count(e => e.Level == LogLevel.Warning),
            Last = failed.Where(e => e.Names(QueueNumber(queues))).Select(e => e.Level).ToArray(),
            Full = logger.Entries.Count(e => KindOf(e) == WarnSetFull && e.Level == LogLevel.Warning),
        }.Should().BeEquivalentTo(
            new { FailedWarnings = 1024, Last = OneDebug, Full = 1 },
            "the record of what was reported holds 1 024 entries: the first 1 024 queues' failures are Warnings, the "
            + "1 025th is logged at Debug, and one Warning says the record is full. "
            + $"Provider asked {provider.Asks.Count} times; {logger.Entries.Length} log entries.");
    }

    [Fact]
    public async Task AgentComplete_ShouldAskExactlyOnceAndOpenNothing_WhenTheMemberStillHasAnotherCallConnected()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromSeconds(10));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.Count }.Should().BeEquivalentTo(
            new { Asks = new[] { (ServerId, Queue) }, WrapUps = 0 },
            "with ringinuse the member finishes one call while another is up: that AgentComplete opens no wrap-up, "
            + "and the provider has still been asked exactly once for it. " + Describe(rig, provider));
    }

    // --- Guards: green on the seam, and must stay green ----------------------------------------------------------

    [Fact]
    public async Task AgentComplete_ShouldCloseAfterWrapUpDuration_WhenNoProviderIsRegistered()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), wrapUpDurations: null, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        rig.WrapUps.Should().BeEquivalentTo(
            [new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp)],
            "Guard: with no provider the wrap-up closes after the global 30 s, as it does without one. "
            + Describe(rig, null));
    }

    [Fact]
    public async Task AgentComplete_ShouldCloseAfterWrapUpDurationAndLogNothing_WhenTheProviderAnswersNull()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var provider = new RecordingProvider((_, _, _) => null);
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            WrapUps = rig.WrapUps.ToArray(),
            ProviderLogs = logger.Entries.Count(e => KindOf(e) is not null),
            Warnings = logger.Entries.Count(e => e.Level >= LogLevel.Warning),
        }.Should().BeEquivalentTo(
            new
            {
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp) },
                ProviderLogs = 0,
                Warnings = 0,
            },
            "Guard: a provider answering null has no value for the queue: the global 30 s applies and nothing is "
            + "logged about the provider. " + Describe(rig, provider, logger));
    }

    [Fact]
    public async Task CallEnded_ShouldNotAskTheProvider_WhenTheWrapUpOfAnAgentKnownByNameOpensAtTheCallsEnd()
    {
        var clock = new FakeTimeProvider();
        var provider = new RecordingProvider((_, _, _) => TimeSpan.FromSeconds(5));
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(AgentLogin("agent-1", Member, "login-1"));
        rig.Deliver(NamedAgentCallConnected("n1", "nm1", Member, "agent-1"));
        clock.Advance(TimeSpan.FromSeconds(45));
        var endedAt = clock.GetUtcNow();
        rig.Deliver(NamedAgentCallEnds("n1", "nm1"));
        var sessionId = rig.Session("n1").SessionId;
        clock.Advance(TimeSpan.FromMinutes(5));

        new { Asks = provider.Asks, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                Asks = Array.Empty<(string, string)>(),
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, endedAt + WrapUp, "agent-1", null, WrapUp) },
            },
            "Guard: a wrap-up the call's end opens (an agent known by name, no queue, no AgentComplete) never asks the "
            + "provider and runs the global 30 s. " + Describe(rig, provider));
    }

    // --- Lock cases: on the threaded fake --------------------------------------------------------------------------

    [Fact]
    public async Task AgentComplete_ShouldOpenTheWrapUp_WhenTheProviderLooksTheCallUpThroughTheManager()
    {
        var clock = new ThreadedTimeProvider();
        CallSessionManager? manager = null;
        string? sessionId = null;
        var lookups = new ConcurrentQueue<bool>();
        var provider = new RecordingProvider((_, _, _) =>
        {
            lookups.Enqueue(manager!.GetById(sessionId!) is not null);
            return TimeSpan.FromSeconds(10);
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        manager = rig.Manager;
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        sessionId = rig.Session("c1").SessionId;
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        await Task.WhenAll(clock.Advance(WrapUp)).WaitAsync(Bound);

        new { WrapUps = rig.WrapUps.Count, AllFound = lookups.All(found => found) }.Should().BeEquivalentTo(
            new { WrapUps = 1, AllFound = true },
            "Guard (a reading convenience only: a same-thread lookup succeeds whether or not a lock is held): a "
            + "provider that looks the call up through the manager gets it, and the wrap-up opens. "
            + Describe(rig, provider));
    }

    [Fact]
    public async Task AgentComplete_ShouldLetAnotherEventOfTheCallBeHandledOnAnotherThread_WhileTheProviderIsStillAnswering()
    {
        var clock = new ThreadedTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider((_, _, _) =>
        {
            entered.TrySetResult();
            gate.Task.Wait(Bound);
            return TimeSpan.FromSeconds(10);
        });
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), provider, logger: null);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var hangup = MemberHangsUp(Queue, "c1", "m1", Member);
        rig.Deliver(hangup[0], hangup[1]);

        var completion = Task.Run(() => rig.Deliver(hangup[2]));
        var providerEntered = await Task.WhenAny(entered.Task, Task.Delay(Bound, TimeProvider.System)) == entered.Task; // fence-allow: GUARD-TIMEOUT — failure bound on the provider being asked, never the winning arm of a green run
        var otherHandledInTime = false;
        Task? other = null;
        if (providerEntered)
        {
            // The caller's own next frames, delivered on another thread while the provider is still answering.
            other = Task.Run(() => rig.Deliver(hangup[3], hangup[4]));
            otherHandledInTime = await Task.WhenAny(other, Task.Delay(Bound, TimeProvider.System)) == other; // fence-allow: GUARD-TIMEOUT — failure bound on the other thread's frames, never the winning arm of a green run
        }

        gate.TrySetResult();
        await completion.WaitAsync(Bound);
        if (other is not null)
            await other.WaitAsync(Bound);

        new { ProviderEntered = providerEntered, OtherHandledInTime = otherHandledInTime }.Should().BeEquivalentTo(
            new { ProviderEntered = true, OtherHandledInTime = true },
            "the provider is asked holding none of the manager's locks: while it is still answering the member's "
            + "AgentComplete, the caller's BridgeLeave and Hangup delivered on another thread are handled without "
            + "waiting for it. " + Describe(rig, provider));
    }

    // --- Frames and helpers ---------------------------------------------------------------------------------------

    private static string QueueNumber(int i) => string.Create(CultureInfo.InvariantCulture, $"q-{i:D4}");

    /// <summary>
    /// Synthetic: a call app_queue connects to <paramref name="member"/> in this file's queue, with no
    /// <c>QueueCallerJoin</c> before it (the manager missed the caller's join); the connect carries an <c>Agent</c>
    /// header only when <paramref name="agent"/> is given.
    /// </summary>
    private static ManagerEvent[] MissedJoinConnected(string caller, string memberLeg, string member, string? agent = null)
    {
        var callerChannel = CallerChannel(caller);
        var memberChannel = MemberChannel(member, memberLeg);
        return
        [
            NewChannel(caller, callerChannel, "Ring", caller, context: "from-trunk", exten: "500"),
            NewChannel(memberLeg, memberChannel, "Down", caller, context: "agents"),
            NewState(memberLeg, "Up"),
            AgentConnect(Queue, caller, callerChannel, caller, member, memberLeg, memberChannel, agent),
            NewState(caller, "Up"),
            BridgeCreate(Bridge(caller)),
            BridgeEnter(Bridge(caller), memberLeg),
            BridgeEnter(Bridge(caller), caller),
        ];
    }

    /// <summary>
    /// Synthetic: the member's <c>AgentComplete</c> for <paramref name="caller"/> with the <c>Queue</c> header removed,
    /// a shape none of the 66 recorded frames has.
    /// </summary>
    private static AgentCompleteEvent AgentCompleteWithoutQueueHeader(string caller, string memberLeg, string member)
    {
        var frame = AgentComplete(Queue, caller, CallerChannel(caller), caller, member, memberLeg,
            MemberChannel(member, memberLeg), 60);
        var fields = new Dictionary<string, string>(frame.RawFields!, StringComparer.OrdinalIgnoreCase);
        fields.Remove("Queue");
        return new AgentCompleteEvent
        {
            EventType = frame.EventType, UniqueId = frame.UniqueId, Channel = frame.Channel, LinkedId = frame.LinkedId,
            Interface = frame.Interface, HoldTime = frame.HoldTime, TalkTime = frame.TalkTime, Reason = frame.Reason,
            DestUniqueId = frame.DestUniqueId, DestChannel = frame.DestChannel, DestLinkedId = frame.DestLinkedId,
            RawFields = fields,
        };
    }

    /// <summary>
    /// Synthetic: the member's <c>AgentComplete</c> for <paramref name="caller"/> with its <c>Queue</c> header set to
    /// <paramref name="queueHeader"/>, a value none of the 66 recorded frames has.
    /// </summary>
    private static AgentCompleteEvent AgentCompleteWithQueueHeader(string caller, string memberLeg, string member,
        string queueHeader)
    {
        var frame = AgentComplete(queueHeader, caller, CallerChannel(caller), caller, member, memberLeg,
            MemberChannel(member, memberLeg), 60);
        return frame.RawFields!["Queue"] == queueHeader
            ? frame
            : throw new InvalidOperationException("premise: the frame carries the Queue header it was given");
    }

    private static string? KindOf(LogEntry entry) =>
        entry.EventId.Name is { } name ? Kinds.FirstOrDefault(kind => name.Contains(kind, StringComparison.Ordinal)) : null;

    private static string Describe(WrapUpRig rig, RecordingProvider? provider, RecordingLogger? logger = null) =>
        "Measured events: [" + string.Join(", ", rig.Events.Select(e => e is CallWrapUpEvent w
            ? $"CallWrapUpEvent({w.SessionId}, {w.ServerId}, {w.Timestamp:HH:mm:ss.fff}, {w.AgentId ?? "null"}, "
              + $"{w.QueueName ?? "null"}, {w.WrapUpDuration})"
            : $"{e.GetType().Name}({e.SessionId})")) + "]"
        + (provider is null ? "" : "; provider asked: [" + string.Join(", ", provider.Asks.Select(a => $"({a.ServerId}, {a.Queue})")) + "]")
        + (logger is null ? "" : "; logs: [" + string.Join(", ", logger.Entries.Select(e => $"{e.Level} {e.EventId.Name}: {e.Message}")) + "]");

    /// <summary>A provider that answers through <c>answer(callIndex, serverId, queueName)</c> and records each call.</summary>
    private sealed class RecordingProvider(Func<int, string, string, TimeSpan?> answer) : IWrapUpDurationProvider
    {
        private readonly ConcurrentQueue<(string ServerId, string Queue)> _asks = new();
        private int _calls;

        public IReadOnlyList<(string ServerId, string Queue)> Asks => [.. _asks];

        public TimeSpan? GetWrapUpDuration(string serverId, string queueName)
        {
            _asks.Enqueue((serverId, queueName));
            return answer(Interlocked.Increment(ref _calls) - 1, serverId, queueName);
        }
    }

    private sealed record LogSummary(LogLevel Level, bool NamesServer, bool NamesQueue);

    /// <summary>Keeps every entry written to it, with its event, message and structured values.</summary>
    private sealed class RecordingLogger : ILogger<CallSessionManager>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public LogEntry[] Entries => _entries.ToArray();

        /// <summary>Every entry of provider-log <paramref name="kind"/>, with whether it names this file's server and queue.</summary>
        public LogSummary[] Of(string kind) =>
        [
            .. Entries.Where(e => KindOf(e) == kind)
                .Select(e => new LogSummary(e.Level, e.Names(ServerId), e.Names(Queue))),
        ];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new List<object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                values.AddRange(pairs.Select(p => p.Value));

            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), values));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, IReadOnlyList<object?> Values)
    {
        /// <summary>Whether <paramref name="value"/> is one of the entry's structured values.</summary>
        public bool Names(object value) => Values.Any(v => Equals(v, value));
    }
}
