using Asterisk.NetAot.Abstractions.Enums;

namespace Asterisk.NetAot.Abstractions;

/// <summary>
/// Represents an async connection to the Asterisk Manager Interface (AMI).
/// </summary>
public interface IAmiConnection : IAsyncDisposable
{
    /// <summary>Current connection state.</summary>
    AmiConnectionState State { get; }

    /// <summary>Asterisk server version detected after login.</summary>
    string? AsteriskVersion { get; }

    /// <summary>Connect and authenticate to the Asterisk AMI.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Send an action and wait for the response.</summary>
    ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default);

    /// <summary>Send an action and wait for a typed response.</summary>
    ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse;

    /// <summary>Send an event-generating action and stream the resulting events.</summary>
    IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, CancellationToken cancellationToken = default);

    /// <summary>Subscribe to all AMI events via IObservable.</summary>
    IDisposable Subscribe(IObserver<ManagerEvent> observer);

    /// <summary>Event raised when an AMI event is received.</summary>
    event Func<ManagerEvent, ValueTask>? OnEvent;

    /// <summary>Fired after a successful automatic reconnection.</summary>
    event Action? Reconnected;

    /// <summary>Gracefully disconnect from the AMI.</summary>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Base class for all AMI manager actions.
/// Concrete actions are source-generated for AOT compatibility.
/// </summary>
public abstract class ManagerAction
{
    public string? ActionId { get; set; }
}

/// <summary>
/// Base class for all AMI manager events.
/// Concrete events are source-generated for AOT compatibility.
/// </summary>
public class ManagerEvent
{
    public string? Privilege { get; set; }
    public string? UniqueId { get; set; }
    public double? Timestamp { get; set; }
    public string? EventType { get; set; }

    /// <summary>Raw fields from the AMI message.</summary>
    public IReadOnlyDictionary<string, string>? RawFields { get; set; }
}

/// <summary>
/// Base class for all AMI manager responses.
/// </summary>
public class ManagerResponse
{
    public string? ActionId { get; set; }
    public string? Response { get; set; }
    public string? Message { get; set; }

    /// <summary>Raw fields from the AMI message.</summary>
    public IReadOnlyDictionary<string, string>? RawFields { get; set; }
}
