using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds the direction the retention bound fails in: it considers only calls that have ended. A call
/// still in progress, however old, is neither released nor altered by it, and calls in progress
/// neither hold back nor hasten the release of the ended ones.
///
/// <para>Releasing a call a consumer still needs is the worst outcome of this bound, which is why the
/// age of a live call is not evidence of anything here: ageing calls in progress out by a clock was
/// measured to mark healthy calls dead. Calls that look stuck are a separate problem with a separate
/// cause.</para>
///
/// <para>Retention is crossed on the manager's clock seam (<see cref="ResidencyRig.MovePastRetention"/>),
/// never by waiting, which puts every call the test opened — live or ended — before the release
/// cutoff. Release is then run by both of its triggers: a leg arriving and a call ending.</para>
/// </summary>
public sealed class LiveCallReleaseTests
{
    /// <summary>Calls in progress held beside the ended ones: many, against a few ended.</summary>
    private const int LiveCalls = 50;

    /// <summary>Ended calls, each opened and ended between two groups of live calls.</summary>
    private const int EndedCalls = 5;

    [Fact]
    public async Task Release_ShouldLeaveAConnectedCallUntouched_WhenItIsOlderThanTheRetentionPeriod()
    {
        await using var rig = new ResidencyRig();
        var live = rig.OpenAnsweredCall("live");
        var before = Snapshot(live);

        rig.MovePastRetention();
        new { Created = live.CreatedAt < rig.Cutoff, Connected = live.ConnectedAt < rig.Cutoff }.Should().BeEquivalentTo(
            new { Created = true, Connected = true },
            $"premise: the call was opened and answered more than the retention period ago. Measured: {rig.Describe()}");

        RunRelease(rig);

        new
        {
            Snapshot = Snapshot(live),
            ById = ReferenceEquals(rig.Manager.GetById(live.SessionId), live),
            ByLinkedId = ReferenceEquals(rig.Manager.GetByLinkedId(live.LinkedId), live),
            ByChannel = live.Participants.Count(p => ReferenceEquals(rig.Manager.GetByChannelId(p.UniqueId), live)),
            Active = rig.Manager.ActiveSessions.Contains(live),
            InStore = ReferenceEquals(await rig.Store.GetAsync(live.SessionId, CancellationToken.None), live),
            Endings = rig.EndingsFor(live.SessionId),
            QueueEntries = rig.QueueEntriesFor(live.SessionId),
        }.Should().BeEquivalentTo(
            new
            {
                Snapshot = before,
                ById = true,
                ByLinkedId = true,
                ByChannel = 2,
                Active = true,
                InStore = true,
                Endings = 0,
                QueueEntries = 0,
            },
            "a connected call is not an ended one, whatever its age: release neither lets it go — by id, "
            + "by linkedid, by either leg's channel, from the active calls or from the default store — nor "
            + "alters it, and nothing ends it or queues it for release. Measured: " + rig.Describe());
    }

    [Fact]
    public async Task Release_ShouldReleaseOnlyTheEndedCalls_WhenManyLiveCallsOlderThanTheRetentionPeriodAreHeld()
    {
        await using var rig = new ResidencyRig();

        // Live calls opened before, between and after the ended ones, so the ended calls lie among
        // calls in progress both older and younger than they are.
        var live = new List<CallSession>(LiveCalls);
        var ended = new List<CallSession>(EndedCalls);
        for (var i = 0; i < EndedCalls; i++)
        {
            for (var j = 0; j < LiveCalls / EndedCalls / 2; j++)
                live.Add(rig.OpenAnsweredCall($"live-{i}-a{j}"));
            ended.Add(rig.Call($"ended-{i}"));
            for (var j = 0; j < LiveCalls / EndedCalls / 2; j++)
                live.Add(rig.OpenAnsweredCall($"live-{i}-b{j}"));
        }

        rig.MovePastRetention();
        RunRelease(rig);

        var endedInStore = 0;
        foreach (var call in ended)
        {
            if (await rig.Store.GetAsync(call.SessionId, CancellationToken.None) is not null)
                endedInStore++;
        }

        var liveInStore = 0;
        foreach (var call in live)
        {
            if (ReferenceEquals(await rig.Store.GetAsync(call.SessionId, CancellationToken.None), call))
                liveInStore++;
        }

        var active = rig.Manager.ActiveSessions.ToHashSet();
        new
        {
            EndedById = ended.Count(s => rig.Manager.GetById(s.SessionId) is not null),
            EndedByLinkedId = ended.Count(s => rig.Manager.GetByLinkedId(s.LinkedId) is not null),
            EndedInStore = endedInStore,
            EndedQueueEntries = ended.Sum(s => rig.QueueEntriesFor(s.SessionId)),
            LiveActive = live.Count(active.Contains),
            LiveById = live.Count(s => ReferenceEquals(rig.Manager.GetById(s.SessionId), s)),
            LiveByLinkedId = live.Count(s => ReferenceEquals(rig.Manager.GetByLinkedId(s.LinkedId), s)),
            LiveConnected = live.Count(s => s.State == CallSessionState.Connected && s.CompletedAt is null),
            LiveInStore = liveInStore,
            LiveEndings = live.Sum(s => rig.EndingsFor(s.SessionId)),
            LiveQueueEntries = live.Sum(s => rig.QueueEntriesFor(s.SessionId)),
        }.Should().BeEquivalentTo(
            new
            {
                EndedById = 0,
                EndedByLinkedId = 0,
                EndedInStore = 0,
                EndedQueueEntries = 0,
                LiveActive = LiveCalls,
                LiveById = LiveCalls,
                LiveByLinkedId = LiveCalls,
                LiveConnected = LiveCalls,
                LiveInStore = LiveCalls,
                LiveEndings = 0,
                LiveQueueEntries = 0,
            },
            $"release considers only the ended calls: all {EndedCalls} of them, past retention, are released "
            + $"although {LiveCalls} calls in progress — older and younger than they are, and all past "
            + "retention too — are held beside them, and every one of those is still active, connected and "
            + $"held where it was. Measured: {rig.Describe()}");
    }

    /// <summary>Runs release by both of its triggers: a leg of a new call arrives, and another call ends.</summary>
    private static void RunRelease(ResidencyRig rig)
    {
        rig.LegJoins("c-arrival", "arrival");
        rig.Call("ending");
    }

    private static CallSnapshot Snapshot(CallSession call) => new(
        call.State,
        call.ConnectedAt,
        call.CompletedAt,
        call.HangupCause,
        call.Participants.Count,
        call.Participants.Count(p => p.LeftAt.HasValue),
        call.Events.Count,
        call.Metadata.Count);

    /// <summary>What release could alter on a call it must leave alone.</summary>
    private sealed record CallSnapshot(
        CallSessionState State,
        DateTimeOffset? ConnectedAt,
        DateTimeOffset? CompletedAt,
        HangupCause? HangupCause,
        int Participants,
        int ParticipantsLeft,
        int AuditEvents,
        int Metadata);
}
