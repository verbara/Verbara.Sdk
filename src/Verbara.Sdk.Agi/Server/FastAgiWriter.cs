using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;

namespace Verbara.Sdk.Agi.Server;

/// <summary>
/// Writes AGI commands to a PipeWriter.
/// Commands are sent as single lines terminated by \n.
/// </summary>
public sealed class FastAgiWriter
{
    private readonly PipeWriter _writer;

    public FastAgiWriter(PipeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>Send an AGI command string.</summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="command"/> contains a line break (CR or LF), which would split one command into
    /// several on the wire. Nothing is written, and the message does not include the command text.
    /// </exception>
    public async ValueTask SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (command.AsSpan().ContainsAny('\r', '\n'))
            ThrowLineBreakInCommand(nameof(command));

        var bytes = Encoding.UTF8.GetByteCount(command) + 1; // +1 for \n
        var span = _writer.GetSpan(bytes);

        var written = Encoding.UTF8.GetBytes(command, span);
        span[written] = (byte)'\n';

        _writer.Advance(written + 1);
        await _writer.FlushAsync(ct);
    }

    [DoesNotReturn]
    private static void ThrowLineBreakInCommand(string paramName) =>
        throw new ArgumentException(
            "The AGI command contains a line break (CR or LF), which would split one command into several on the wire. The command text is not shown.",
            paramName);
}
