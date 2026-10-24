using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// The lock order of the channel table, pinned on two threads: a reload thread inside
/// <see cref="ChannelManager.ChannelAdded"/> for a channel it is admitting hangs up another channel,
/// while the AMI thread hangs up the channel being admitted. Both hangups must get through without
/// announcing the admitted channel's removal before its admission. The two channels' removals may
/// race; neither the starting signal nor removal from an index fixes their posting order. Each step is released by a
/// <see cref="TaskCompletionSource"/> and every round is joined under a bound, so a deadlock fails
/// as a timeout rather than hanging the run.
/// </summary>
public sealed class ChannelManagerLockOrderTests(ITestOutputHelper output)
{
    private const int Rounds = 2_000;

    /// <summary>Failure bound for one round and for each hand-off inside it.</summary>
    private static readonly TimeSpan RoundBound = TimeSpan.FromSeconds(10);

    private const string Admitted = "1700000000.10";
    private const string AdmittedName = "PJSIP/2000-0010";
    private const string Held = "1700000000.20";
    private const string HeldName = "PJSIP/3000-0020";

    [Fact]
    public async Task OnHangup_ShouldNeitherDeadlockNorAnnounceTheRemovalFirst_WhenASubscriberHangsUpAnotherChannelWhileTheAdmittedOneHangsUp()
    {
        var redRounds = 0;
        string? firstRed = null;
        var roundsWhereTheHangupHadAlreadyRemovedIt = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var manager = new ChannelManager(NullLogger.Instance);
            manager.OnNewChannel(Held, HeldName, ChannelState.Up);

            var order = new List<string>(4);
            var gate = new Lock();
            var insideAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var amiHangupStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyRemoved = false;

            manager.ChannelAdded += channel =>
            {
                if (channel.UniqueId != Admitted)
                    return;
                lock (gate)
                {
                    order.Add("added " + Admitted);
                }

                insideAdded.TrySetResult();
                if (!amiHangupStarting.Task.Wait(RoundBound))
                    throw new TimeoutException("the AMI thread never started its hangup");

                alreadyRemoved = manager.GetByUniqueId(Admitted) is null;
                manager.OnHangup(Held, HangupCause.NormalClearing);
            };
            manager.ChannelRemoved += channel =>
            {
                lock (gate)
                {
                    order.Add("removed " + channel.UniqueId);
                }
            };

            using (var window = manager.OpenReadWindow())
            {
                var reload = Task.Run(() => manager.ReconcileWithSnapshot(
                    [
                        new ChannelSnapshotEntry(Admitted, AdmittedName, ChannelState.Up),
                        new ChannelSnapshotEntry(Held, HeldName, ChannelState.Up),
                    ],
                    window));
                var ami = Task.Run(async () =>
                {
                    await insideAdded.Task.WaitAsync(RoundBound);
                    amiHangupStarting.TrySetResult();
                    manager.OnHangup(Admitted, HangupCause.NormalClearing);
                });

                await Task.WhenAll(reload, ami).WaitAsync(RoundBound);
            }

            if (alreadyRemoved)
                roundsWhereTheHangupHadAlreadyRemovedIt++;

            string[] seen;
            lock (gate)
            {
                seen = [.. order];
            }

            var consistent = seen.Length == 3
                && seen[0] == "added " + Admitted
                && seen.Count(item => item == "removed " + Admitted) == 1
                && seen.Count(item => item == "removed " + Held) == 1
                && manager.ChannelCount == 0
                && manager.GetByName(AdmittedName) is null
                && manager.GetByName(HeldName) is null;
            if (!consistent)
            {
                redRounds++;
                firstRed ??= $"round {round}: announced [{string.Join(", ", seen)}], {manager.ChannelCount} held";
            }
        }

        output.WriteLine(
            $"{roundsWhereTheHangupHadAlreadyRemovedIt} of {Rounds} rounds: the AMI thread's hangup had already "
            + "taken the admitted channel out of the table when the subscriber hung up the other one");

        redRounds.Should().Be(0,
            "the subscriber's hangup and the AMI hangup both get through, the admitted channel's removal is "
            + "announced after its admission, each removal is announced once, and the held channel that hung up "
            + $"during the read is not admitted again ({Rounds} rounds); first red round: {firstRed}");
    }
}
