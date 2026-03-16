using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk;

/// <summary>
/// Represents an async FastAGI server that accepts AGI connections from Asterisk.
/// </summary>
public interface IAgiServer : IAsyncDisposable
{
    /// <summary>Port the server listens on (default: 4573).</summary>
    int Port { get; }

    /// <summary>Whether the server is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Current server lifecycle state.</summary>
    AgiServerState State { get; }

    /// <summary>Start accepting AGI connections.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop accepting connections and shutdown gracefully.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An AGI script that handles incoming AGI requests.
/// </summary>
public interface IAgiScript
{
    /// <summary>Execute the AGI script logic.</summary>
    ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an AGI channel for sending AGI commands.
/// </summary>
public interface IAgiChannel
{
    ValueTask AnswerAsync(CancellationToken cancellationToken = default);
    ValueTask HangupAsync(CancellationToken cancellationToken = default);
    ValueTask<string> GetVariableAsync(string name, CancellationToken cancellationToken = default);
    ValueTask SetVariableAsync(string name, string value, CancellationToken cancellationToken = default);
    ValueTask<char> StreamFileAsync(string file, string escapeDigits = "", CancellationToken cancellationToken = default);
    ValueTask<string> GetDataAsync(string file, int timeout = 0, int maxDigits = 0, CancellationToken cancellationToken = default);
    ValueTask ExecAsync(string application, string args = "", CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an incoming AGI request from Asterisk.
/// </summary>
public interface IAgiRequest
{
    string? Script { get; }
    string? Channel { get; }
    string? UniqueId { get; }
    string? CallerId { get; }
    string? CallerIdName { get; }
    string? Context { get; }
    string? Extension { get; }
    int Priority { get; }
    string? Language { get; }
    bool IsNetwork { get; }
}
