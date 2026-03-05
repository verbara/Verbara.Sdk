using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Commands;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Agi.Server;

internal static partial class AgiChannelLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI_CMD] Sending: command={Command}")]
    public static partial void CommandSending(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI_CMD] Reply: command={Command} status_code={StatusCode} result={Result} raw={RawLine}")]
    public static partial void ReplyReceived(ILogger logger, string command, int statusCode, string result, string rawLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AGI_CMD] Failed: command={Command} status_code={StatusCode} raw={RawLine}")]
    public static partial void CommandFailed(ILogger logger, string command, int statusCode, string rawLine);
}

/// <summary>
/// AGI channel implementation. Sends commands via writer and reads replies via reader.
/// </summary>
public sealed class AgiChannel : IAgiChannel
{
    private readonly FastAgiWriter _writer;
    private readonly FastAgiReader _reader;
    private readonly ILogger? _logger;

    public AgiChannel(FastAgiWriter writer, FastAgiReader reader, ILogger? logger = null)
    {
        _writer = writer;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>Send an AGI command and wait for the reply.</summary>
    public async ValueTask<AgiReply> SendCommandAsync(AgiCommandBase command, CancellationToken cancellationToken = default)
    {
        var cmd = command.BuildCommand();
        if (_logger is not null) AgiChannelLog.CommandSending(_logger, cmd);

        await _writer.SendCommandAsync(cmd, cancellationToken);
        var reply = await _reader.ReadReplyAsync(cancellationToken)
            ?? throw new AgiException("Connection closed while waiting for reply");

        LogReply(cmd, reply);
        return reply;
    }

    /// <summary>Send a raw command string and wait for the reply.</summary>
    public async ValueTask<AgiReply> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_logger is not null) AgiChannelLog.CommandSending(_logger, command);

        await _writer.SendCommandAsync(command, cancellationToken);
        var reply = await _reader.ReadReplyAsync(cancellationToken)
            ?? throw new AgiException("Connection closed while waiting for reply");

        LogReply(command, reply);
        return reply;
    }

    private void LogReply(string command, AgiReply reply)
    {
        if (_logger is null) return;

        if (reply.IsSuccess)
            AgiChannelLog.ReplyReceived(_logger, command, reply.StatusCode, reply.Result, reply.RawLine);
        else
            AgiChannelLog.CommandFailed(_logger, command, reply.StatusCode, reply.RawLine);
    }

    public async ValueTask AnswerAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("ANSWER", cancellationToken);
        if (!reply.IsSuccess) throw new AgiException($"ANSWER failed: {reply.RawLine}");
    }

    public async ValueTask HangupAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("HANGUP", cancellationToken);
    }

    public async ValueTask<string> GetVariableAsync(string name, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync($"GET VARIABLE {name}", cancellationToken);
        return reply.Extra ?? reply.Result;
    }

    public async ValueTask SetVariableAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync($"SET VARIABLE {name} \"{value}\"", cancellationToken);
    }

    public async ValueTask<char> StreamFileAsync(string file, string escapeDigits = "", CancellationToken cancellationToken = default)
    {
        var cmd = string.IsNullOrEmpty(escapeDigits)
            ? $"STREAM FILE {file} \"\""
            : $"STREAM FILE {file} \"{escapeDigits}\"";
        var reply = await SendCommandAsync(cmd, cancellationToken);
        return reply.ResultAsChar;
    }

    public async ValueTask<string> GetDataAsync(string file, int timeout = 0, int maxDigits = 0, CancellationToken cancellationToken = default)
    {
        var cmd = timeout > 0
            ? (maxDigits > 0 ? $"GET DATA {file} {timeout} {maxDigits}" : $"GET DATA {file} {timeout}")
            : $"GET DATA {file}";
        var reply = await SendCommandAsync(cmd, cancellationToken);
        return reply.Result;
    }

    public async ValueTask ExecAsync(string application, string args = "", CancellationToken cancellationToken = default)
    {
        var cmd = string.IsNullOrEmpty(args) ? $"EXEC {application}" : $"EXEC {application} {args}";
        await SendCommandAsync(cmd, cancellationToken);
    }
}
