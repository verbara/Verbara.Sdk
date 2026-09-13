using Verbara.Sdk.Enums;

namespace Verbara.Sdk;

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
    /// <summary>Send ANSWER.</summary>
    ValueTask AnswerAsync(CancellationToken cancellationToken = default);

    /// <summary>Send HANGUP.</summary>
    ValueTask HangupAsync(CancellationToken cancellationToken = default);

    /// <summary>Send GET VARIABLE and return the variable's value.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="name"/> contains a line break (CR or LF), which would split the command into
    /// several on the wire. Nothing is sent.
    /// </exception>
    ValueTask<string> GetVariableAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Send SET VARIABLE.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="name"/> or <paramref name="value"/> contains a line break (CR or LF), which
    /// would split the command into several on the wire. Nothing is sent.
    /// </exception>
    ValueTask SetVariableAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Send STREAM FILE and return the digit that stopped playback, if any.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="file"/> or <paramref name="escapeDigits"/> contains a line break (CR or LF),
    /// which would split the command into several on the wire. Nothing is sent.
    /// </exception>
    ValueTask<char> StreamFileAsync(string file, string escapeDigits = "", CancellationToken cancellationToken = default);

    /// <summary>Send GET DATA and return the digits received.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="file"/> contains a line break (CR or LF), which would split the command into
    /// several on the wire. Nothing is sent.
    /// </exception>
    ValueTask<string> GetDataAsync(string file, int timeout = 0, int maxDigits = 0, CancellationToken cancellationToken = default);

    /// <summary>Send EXEC to run a dialplan application.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="application"/> or <paramref name="args"/> contains a line break (CR or LF),
    /// which would split the command into several on the wire. Nothing is sent.
    /// </exception>
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
