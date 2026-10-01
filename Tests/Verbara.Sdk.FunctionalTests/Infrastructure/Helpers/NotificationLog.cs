namespace Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Globalization;
using Verbara.Sdk.Enums;

/// <summary>
/// Records, in delivery order, what an AMI connection announces on <see cref="IAmiConnection.StateChanged"/> and
/// <see cref="IAmiConnection.Reconnected"/>, which share one notification queue, and signals the deliveries a test
/// waits for.
/// </summary>
public sealed class NotificationLog
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TaskCompletionSource _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AmiConnectionStateChange> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first <see cref="IAmiConnection.Reconnected"/> is delivered.</summary>
    public Task Reconnected => _reconnected.Task;

    /// <summary>Completes when the first change to <see cref="AmiConnectionState.Disconnected"/> is delivered.</summary>
    public Task Disconnected => _disconnected.Task;

    /// <summary>Completes with the first change whose <see cref="AmiConnectionStateChange.IsFinal"/> is set.</summary>
    public Task<AmiConnectionStateChange> Final => _final.Task;

    /// <summary>A <see cref="IAmiConnection.StateChanged"/> handler.</summary>
    public void Add(AmiConnectionStateChange change)
    {
        lock (_gate)
            _entries.Add(new Entry(change));

        if (change.Current == AmiConnectionState.Disconnected)
            _disconnected.TrySetResult();
        if (change.IsFinal)
            _final.TrySetResult(change);
    }

    /// <summary>A <see cref="IAmiConnection.Reconnected"/> handler.</summary>
    public void AddReconnected()
    {
        lock (_gate)
            _entries.Add(new Entry(null));

        _reconnected.TrySetResult();
    }

    /// <summary>What has been delivered so far, in delivery order.</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate)
            return [.. _entries];
    }

    /// <summary>One delivery: a state change, or <see cref="IAmiConnection.Reconnected"/> when <see cref="Change"/> is null.</summary>
    public sealed record Entry(AmiConnectionStateChange? Change)
    {
        public override string ToString() => Change is null
            ? "Reconnected"
            : string.Create(CultureInfo.InvariantCulture,
                $"StateChanged {Change.Previous} -> {Change.Current} byCaller={Change.ByCaller} isLoss={Change.IsLoss} isFinal={Change.IsFinal} cause={Change.Cause?.GetType().Name ?? "null"}: {Change.Cause?.Message}");
    }
}
