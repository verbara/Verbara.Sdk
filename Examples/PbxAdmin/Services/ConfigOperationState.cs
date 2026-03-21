namespace PbxAdmin.Services;

/// <summary>
/// Shared state that tracks in-flight config operations (slow AMI actions).
/// Pages set the operation before calling PbxConfigManager methods;
/// the MainLayout overlay observes changes and shows a progress indicator.
/// </summary>
public sealed class ConfigOperationState : IConfigOperationState
{
    private volatile ConfigOperation? _current;

    public ConfigOperation? Current => _current;
    public bool IsBusy => _current is not null;

    public event Action? OnChanged;

    /// <summary>Starts tracking a config operation. Returns a disposable scope that clears it on dispose.</summary>
    public IDisposable Begin(string operation, string serverId, string? detail = null)
    {
        _current = new ConfigOperation(operation, serverId, detail, DateTimeOffset.UtcNow);
        OnChanged?.Invoke();
        return new Scope(this);
    }

    private void End()
    {
        _current = null;
        OnChanged?.Invoke();
    }

    private sealed class Scope(ConfigOperationState state) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                state.End();
        }
    }
}

public sealed record ConfigOperation(
    string Operation,
    string ServerId,
    string? Detail,
    DateTimeOffset StartedAt)
{
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartedAt;
}
