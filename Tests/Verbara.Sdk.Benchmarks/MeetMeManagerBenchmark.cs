using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of the conference manager's most frequent event path (a participant's mute state flipping), with
/// and without one subscriber. Each row reports the cost of ONE event (<c>OperationsPerInvoke</c>), so its allocated
/// bytes are per event and deterministic. The mute rows reuse participants built in setup, so they cannot see the size of
/// <see cref="MeetMeUser"/>; <see cref="JoinLeave"/> and <see cref="Reconcile_RemovesParticipants"/> build it inside the
/// measured operation, in a room a held participant keeps alive, so the room is not rebuilt.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MeetMeManagerBenchmark
{
    private const int N = 1000;
    private const string ChurnRoom = "200";
    private const string HeldChannel = "PJSIP/held-00000000";
    private string[] _chans = null!;
    private MeetMeManager _unobserved = null!;
    private MeetMeManager _observed = null!;
    private MeetMeManager _joinLeave = null!;
    private MeetMeManager _reconciled = null!;
    private string[] _listedRooms = null!;
    private Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>> _heldOnly = null!;
    private bool _flip;
    private int _seen;

    [GlobalSetup]
    public void Setup()
    {
        _chans = new string[N];
        for (var i = 0; i < N; i++)
            _chans[i] = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);

        _unobserved = new MeetMeManager(NullLogger.Instance);
        _observed = new MeetMeManager(NullLogger.Instance);
        _observed.UserStateChanged += (_, _) => _seen++;
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnUserJoined("100", 0, _chans[i]);
            _observed.OnUserJoined("100", 0, _chans[i]);
        }

        _joinLeave = new MeetMeManager(NullLogger.Instance);
        _joinLeave.OnUserJoined(ChurnRoom, 0, HeldChannel);
        _reconciled = new MeetMeManager(NullLogger.Instance);
        _reconciled.OnUserJoined(ChurnRoom, 0, HeldChannel);
        _listedRooms = [ChurnRoom];
        _heldOnly = new Dictionary<string, IReadOnlyList<ConferenceSnapshotParticipant>>
        {
            [ChurnRoom] = [new ConferenceSnapshotParticipant(HeldChannel, null, false, false)],
        };
    }

    // The flag flips on every invocation so every call is a real change, never a repeat the manager ignores.
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void Mute_NoSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _unobserved.OnUserMuted("100", _chans[i], _flip);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Mute_OneSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _observed.OnUserMuted("100", _chans[i], _flip);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void JoinLeave()
    {
        for (var i = 0; i < N; i++)
        {
            _joinLeave.OnUserJoined(ChurnRoom, 0, _chans[i]);
            _joinLeave.OnUserLeft(ChurnRoom, _chans[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Reconcile_RemovesParticipants()
    {
        for (var i = 0; i < N; i++)
            _reconciled.OnUserJoined(ChurnRoom, 0, _chans[i]);

        using var window = _reconciled.OpenSnapshotWindow();
        _reconciled.ReconcileWithSnapshot(window, _listedRooms, _heldOnly, removeOmitted: true);
    }
}
