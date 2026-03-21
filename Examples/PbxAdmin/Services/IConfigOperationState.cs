namespace PbxAdmin.Services;

/// <summary>
/// Shared state that tracks in-flight config operations (slow AMI actions).
/// </summary>
public interface IConfigOperationState
{
    ConfigOperation? Current { get; }
    bool IsBusy { get; }
    event Action? OnChanged;
    IDisposable Begin(string operation, string serverId, string? detail = null);
}
