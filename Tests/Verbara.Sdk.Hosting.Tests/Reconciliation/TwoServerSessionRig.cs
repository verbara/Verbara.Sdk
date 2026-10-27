using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// An in-process two-server session rig composed from <see cref="PoolSweepRig"/>. It starts no background sweep.
/// Every test using it must dispose it, normally through xunit's IAsyncLifetime, to check the actual index invariant.
/// </summary>
/// <remarks>
/// Drive serial operations directly, and register every concurrent producer with <see cref="Track"/>. When a scenario
/// defers notifications, also register its explicit causal notification fence with <see cref="AfterNotification"/> and
/// release its gates before cleanup. Awaited driver returns alone are not a general outbox ordering guarantee; this rig
/// does not repair or hide bounded-outbox arrival/removal overtaking. Never use a delay as a notification fence.
/// </remarks>
internal sealed class TwoServerSessionRig : IAsyncDisposable
{
    private readonly List<Task> _producers = [];
    private readonly List<Task> _notifications = [];

    public TwoServerSessionRig(Action<SessionOptions>? configure = null)
    {
        Pool = new PoolSweepRig(configure: configure);
        A = Pool.AddServer("a");
        B = Pool.AddServer("b");
    }

    public PoolSweepRig Pool { get; }
    public RigServer A { get; }
    public RigServer B { get; }
    public CallSessionManager Manager => Pool.Manager;

    public Task Track(Task producer)
    {
        _producers.Add(producer);
        return producer;
    }

    public void AfterNotification(Task notification) => _notifications.Add(notification);

    /// <summary>Drives raw serial arrivals without requiring that the current engine opened a call.</summary>
    public static void AddChannels(RigServer server, string linkedId, params string[] channelIds)
    {
        foreach (var id in channelIds)
            server.Server.Channels.OnNewChannel(id, $"PJSIP/100-{id}", ChannelState.Up, linkedId: linkedId);
    }

    /// <summary>Inspects actual linked-id entries for this owner; this is not the future public scoped API.</summary>
    public CallSession? FindCurrentByLinkedId(string serverId, string linkedId) => Manager.InspectIndexes().LinkedIds
        .FirstOrDefault(e => e.Key == linkedId && e.Value.ServerId == serverId).Value;

    /// <summary>Opens legs and requires their server's call. Use AddChannels to test missing/colliding admissions.</summary>
    public CallSession Open(RigServer server, string linkedId, params string[] channelIds)
    {
        AddChannels(server, linkedId, channelIds);
        return FindCurrentByLinkedId(server.Id, linkedId)
            ?? throw new InvalidOperationException($"No call was opened for '{linkedId}' on '{server.Id}'.");
    }

    public async Task AssertIndexesAsync()
    {
        await Task.WhenAll(_producers).WaitAsync(SweepRig.Bound);
        await Task.WhenAll(_notifications).WaitAsync(SweepRig.Bound);
        SessionIndexInvariant.Assert(Manager);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await AssertIndexesAsync();
        }
        finally
        {
            await Pool.DisposeAsync();
        }
    }
}
