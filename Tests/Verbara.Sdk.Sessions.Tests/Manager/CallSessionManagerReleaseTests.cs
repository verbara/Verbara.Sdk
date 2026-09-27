using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Binds that the release of ended calls makes progress whatever the condition of the entries it
/// walks: an entry it cannot evaluate is discarded, and every other entry old enough to be released
/// is released in the same walk.
///
/// <para>The manager queues each call whose ending it delivered and walks that queue, oldest first,
/// releasing what is past <see cref="SessionOptions.CompletedRetention"/>. A walk that takes an
/// entry off only when it can evaluate it stops at the first one it cannot — for good, because the
/// same entry is at the head of every later walk. Two such heads are known: an entry naming a
/// session the manager no longer holds (the second copy of an ending delivered twice, once the
/// first copy has released the call), and an entry with no completion time. A third head stops such
/// a walk just as surely although it can be evaluated: a call whose public
/// <see cref="CallSession.CompletedAt"/> a consumer moved forward after its ending, if the walk reads
/// that property again instead of the time recorded when the ending was queued.</para>
///
/// <para>These tests put each bad head at the front directly, through the manager's own enqueue
/// step, rather than through a route that produces it: the walk's guarantee must not depend on
/// which route — today's or a future one — made the head bad. The functional tests
/// (<c>ReleaseWedgeTests</c>) cover the routes. Retention is crossed on the manager's clock seam,
/// never by waiting, and each test asserts on what a single walk did with the bad head in front, so
/// a walk that got past it only on a later call would still fail.</para>
/// </summary>
public sealed class CallSessionManagerReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Retention = new SessionOptions().CompletedRetention;

    /// <summary>A completion time past retention on the manager's clock.</summary>
    private static readonly DateTimeOffset LongAgo = Now - Retention - TimeSpan.FromMinutes(1);

    /// <summary>How many eligible calls are queued behind the bad head.</summary>
    private const int Eligible = 3;

    [Fact]
    public async Task EvictStaleCompleted_ShouldReleaseEveryOtherEligibleEntry_WhenTheHeadNamesASessionNoLongerHeld()
    {
        await using var sut = NewManager();

        // The call is released by its first entry; a second copy of its ending, queued afterwards,
        // is then at the head and names a session the manager no longer holds.
        var gone = HeldAndQueued(sut, "gone", LongAgo);
        sut.EvictStaleCompleted();
        sut.GetById(gone.SessionId).Should().BeNull("premise: the first entry released the call");

        // Once released, its linkedid is free: a leg carrying it opens a new, live call.
        var reuser = new CallSession("s-reuser", gone.LinkedId, "srv-1", CallDirection.Inbound)
        {
            State = CallSessionState.Connected,
        };
        sut.RegisterReconstructedSession(reuser).Should().BeTrue("premise: the released call's linkedid is free");

        sut.QueueForRelease(gone);
        var eligible = Enumerable.Range(0, Eligible).Select(i => HeldAndQueued(sut, $"old{i}", LongAgo)).ToList();
        var recent = HeldAndQueued(sut, "recent", Now);

        sut.EvictStaleCompleted();

        new
        {
            HeadEntries = sut.ReleaseQueueEntriesFor(gone.SessionId),
            LiveCallByLinkedId = sut.GetByLinkedId(gone.LinkedId)?.SessionId,
            EligibleStillHeld = eligible.Count(s => IsHeld(sut, s)),
            RecentHeld = IsHeld(sut, recent),
            RecentEntries = sut.ReleaseQueueEntriesFor(recent.SessionId),
        }.Should().BeEquivalentTo(
            new
            {
                HeadEntries = 0,
                LiveCallByLinkedId = (string?)reuser.SessionId,
                EligibleStillHeld = 0,
                RecentHeld = true,
                RecentEntries = 1,
            },
            "an entry naming a session no longer held is discarded without touching anything — not even "
            + "the live call now carrying the same linkedid — every entry behind it that is past retention "
            + "is released in the same walk, and the walk still stops at a call within retention");
    }

    [Fact]
    public async Task EvictStaleCompleted_ShouldReleaseEveryOtherEligibleEntry_WhenTheHeadCarriesNoCompletionTime()
    {
        await using var sut = NewManager();

        var timeless = HeldAndQueued(sut, "timeless", completedAt: null);
        var eligible = Enumerable.Range(0, Eligible).Select(i => HeldAndQueued(sut, $"old{i}", LongAgo)).ToList();
        var recent = HeldAndQueued(sut, "recent", Now);

        sut.EvictStaleCompleted();

        new
        {
            HeadEntries = sut.ReleaseQueueEntriesFor(timeless.SessionId),
            HeadCallHeld = IsHeld(sut, timeless),
            EligibleStillHeld = eligible.Count(s => IsHeld(sut, s)),
            RecentHeld = IsHeld(sut, recent),
            RecentEntries = sut.ReleaseQueueEntriesFor(recent.SessionId),
        }.Should().BeEquivalentTo(
            new { HeadEntries = 0, HeadCallHeld = true, EligibleStillHeld = 0, RecentHeld = true, RecentEntries = 1 },
            "an entry with no completion time is discarded — and its call kept, since nothing says how "
            + "old it is — while every entry behind it that is past retention is released in the same "
            + "walk, and the walk still stops at a call within retention");
    }

    [Fact]
    public async Task EvictStaleCompleted_ShouldReleaseEveryOtherEligibleEntry_WhenAConsumerMovedTheHeadsCompletionTimeForward()
    {
        await using var sut = NewManager();

        // CallSession.CompletedAt has a public setter. A walk that reads it again, instead of the time
        // recorded when the ending was queued, would judge this head inside retention on every walk
        // from now on — the same wedge as a head it cannot evaluate, from a head it can.
        var moved = HeldAndQueued(sut, "moved", LongAgo);
        moved.CompletedAt = DateTimeOffset.MaxValue;
        var eligible = Enumerable.Range(0, Eligible).Select(i => HeldAndQueued(sut, $"old{i}", LongAgo)).ToList();
        var recent = HeldAndQueued(sut, "recent", Now);

        sut.EvictStaleCompleted();

        new
        {
            HeadCallHeld = IsHeld(sut, moved),
            HeadEntries = sut.ReleaseQueueEntriesFor(moved.SessionId),
            EligibleStillHeld = eligible.Count(s => IsHeld(sut, s)),
            RecentHeld = IsHeld(sut, recent),
            RecentEntries = sut.ReleaseQueueEntriesFor(recent.SessionId),
        }.Should().BeEquivalentTo(
            new { HeadCallHeld = false, HeadEntries = 0, EligibleStillHeld = 0, RecentHeld = true, RecentEntries = 1 },
            "an entry is judged by the completion time recorded when its ending was queued, so a call "
            + "that ended past retention is released whatever its CompletedAt says now, and every entry "
            + "behind it that is past retention is released in the same walk");
    }

    private static CallSessionManager NewManager() =>
        new(
            Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore(),
            new FixedClock(Now));

    /// <summary>
    /// An ended call the manager holds, queued for release as its ending would queue it, whose
    /// completion time is <paramref name="completedAt"/>.
    /// </summary>
    private static CallSession HeldAndQueued(CallSessionManager sut, string tag, DateTimeOffset? completedAt)
    {
        var session = new CallSession($"s-{tag}", $"L-{tag}", "srv-1", CallDirection.Inbound)
        {
            State = CallSessionState.Completed,
            CompletedAt = completedAt,
        };
        sut.RegisterReconstructedSession(session).Should().BeTrue($"premise: '{tag}' is a new call");
        sut.QueueForRelease(session);
        return session;
    }

    private static bool IsHeld(CallSessionManager sut, CallSession session) =>
        sut.GetById(session.SessionId) is not null || sut.GetByLinkedId(session.LinkedId) is not null;

    /// <summary>The manager's release clock, standing still: the tests move nothing, they place calls in time.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
