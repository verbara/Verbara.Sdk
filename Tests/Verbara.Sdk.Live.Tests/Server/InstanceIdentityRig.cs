using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>What one subscriber read off a notification at the moment it received it.</summary>
/// <param name="Identity">The instance identity, read at receipt; <see langword="null"/> when it named no instance.</param>
/// <param name="SystemName">The identity's system name, read at receipt.</param>
internal sealed record IdentityReading(SessionDomainEvent Notification, SessionEventEpoch? Epoch, object? Identity,
    string? SystemName);

/// <summary>
/// Records every notification a subscriber receives, with the loss mark and the instance identity it read at that
/// moment, so a test can tell an identity sealed before the first subscriber from one set afterwards; and lets a test
/// wait for the first one of a kind that matches.
/// </summary>
/// <remarks>The identity is read only through <see cref="InstanceIdentityNames"/>.</remarks>
internal sealed class IdentityRecorder : IObserver<SessionDomainEvent>
{
    private readonly Lock _gate = new();
    private readonly List<IdentityReading> _seen = [];
    private readonly List<(Func<SessionDomainEvent, bool> Match, TaskCompletionSource<IdentityReading> Signal)> _waiters = [];

    public static IdentityRecorder On(CallSessionManager manager)
    {
        var recorder = new IdentityRecorder();
        manager.Events.Subscribe(recorder);
        return recorder;
    }

    public IReadOnlyList<IdentityReading> Seen
    {
        get
        {
            lock (_gate)
                return [.. _seen];
        }
    }

    /// <summary>The reading of the first notification of kind <typeparamref name="T"/> that matches, once it arrives.</summary>
    public async Task<IdentityReading> WaitAsync<T>(Func<T, bool>? match = null) where T : SessionDomainEvent
    {
        bool Matches(SessionDomainEvent e) => e is T typed && (match is null || match(typed));
        TaskCompletionSource<IdentityReading> signal;
        lock (_gate)
        {
            foreach (var reading in _seen)
                if (Matches(reading.Notification))
                    return reading;
            signal = new TaskCompletionSource<IdentityReading>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((Matches, signal));
        }
        return await signal.Task.WaitAsync(Run.Bound);
    }

    public void OnNext(SessionDomainEvent value)
    {
        var reading = new IdentityReading(value, value.Epoch, InstanceIdentityNames.IdentityOf(value),
            InstanceIdentityNames.SystemNameOf(value));
        lock (_gate)
        {
            _seen.Add(reading);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(value))
                    continue;
                _waiters[i].Signal.TrySetResult(reading);
                _waiters.RemoveAt(i);
            }
        }
    }

    public void OnError(Exception error)
    {
        // The engine never faults its subscribers in these tests; nothing to record.
    }

    public void OnCompleted()
    {
        // Completed when the engine is disposed; nothing to record.
    }
}

/// <summary>
/// Completes once the connection's state first leaves <see cref="AmiConnectionState.Connected"/>: the AMI session was
/// lost, so a caller's own connect may follow. A signal, never a poll.
/// </summary>
internal sealed class SessionLostSignal
{
    private readonly TaskCompletionSource _left = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Left => _left.Task;

    public void OnStateChanged(AmiConnectionStateChange change)
    {
        if (change.Previous == AmiConnectionState.Connected && change.Current != AmiConnectionState.Connected)
            _left.TrySetResult();
    }
}

/// <summary>How the new AMI session of a reconnect test is opened.</summary>
public enum ReconnectRoute
{
    /// <summary>The connection's reconnect loop (<c>AutoReconnect = true</c>).</summary>
    Loop,

    /// <summary>The caller's own <c>ConnectAsync</c> after the loss (<c>AutoReconnect = false</c>).</summary>
    CallerConnect,
}
