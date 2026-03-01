using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Agi.Commands;

namespace Asterisk.NetAot.Agi.Server;

/// <summary>
/// AGI channel implementation. Sends commands via writer and reads replies via reader.
/// </summary>
public sealed class AgiChannel : IAgiChannel
{
    private readonly FastAgiWriter _writer;
    private readonly FastAgiReader _reader;

    public AgiChannel(FastAgiWriter writer, FastAgiReader reader)
    {
        _writer = writer;
        _reader = reader;
    }

    /// <summary>Send an AGI command and wait for the reply.</summary>
    public async ValueTask<AgiReply> SendCommandAsync(AgiCommandBase command, CancellationToken cancellationToken = default)
    {
        await _writer.SendCommandAsync(command.BuildCommand(), cancellationToken);
        var reply = await _reader.ReadReplyAsync(cancellationToken)
            ?? throw new AgiException("Connection closed while waiting for reply");
        return reply;
    }

    /// <summary>Send a raw command string and wait for the reply.</summary>
    public async ValueTask<AgiReply> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _writer.SendCommandAsync(command, cancellationToken);
        var reply = await _reader.ReadReplyAsync(cancellationToken)
            ?? throw new AgiException("Connection closed while waiting for reply");
        return reply;
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
