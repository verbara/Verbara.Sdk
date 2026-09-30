using System.Collections.Concurrent;

namespace Verbara.Sdk.Tests.Shared.Sockets;

/// <summary>
/// Records every task exception that went unobserved while it is subscribed. The subscription is
/// process-wide, so <see cref="Faults"/> keeps only those whose stack names the server type given to
/// the constructor (<c>nameof(FastAgiServer)</c>, <c>nameof(AudioSocketServer)</c>): a fault from
/// another test class running in parallel stays out of this one's assertion. <see cref="All"/> keeps
/// everything, for the failure message.
/// </summary>
/// <remarks>Linked into each suite that needs it (<c>&lt;Compile Include=… Link=…&gt;</c>); one definition.</remarks>
internal sealed class UnobservedServerFaults : IDisposable
{
    private readonly ConcurrentQueue<Exception> _seen = new();
    private readonly string _serverTypeName;

    public UnobservedServerFaults(string serverTypeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverTypeName);
        _serverTypeName = serverTypeName;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    /// <summary>The unobserved exceptions whose stack names the server type this recorder was given.</summary>
    public IReadOnlyList<string> Faults =>
    [
        .. _seen
            .Where(ex => ex.StackTrace?.Contains(_serverTypeName, StringComparison.Ordinal) == true)
            .Select(ex => $"{ex.GetType().Name}: {ex.Message}")
    ];

    /// <summary>Every unobserved exception seen, whatever threw it.</summary>
    public IReadOnlyList<string> All => [.. _seen.Select(ex => $"{ex.GetType().Name}: {ex.Message}")];

    /// <summary>
    /// Collects, so a faulted task that nothing references any more is finalised and its
    /// exception, if nothing observed it, is published before this returns. Call it only once the
    /// task in question has completed.
    /// </summary>
    public static void CollectDiscardedTasks()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var inner in e.Exception.InnerExceptions)
            _seen.Enqueue(inner);
    }
}
