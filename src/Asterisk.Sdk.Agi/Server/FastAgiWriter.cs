using System.IO.Pipelines;
using System.Text;

namespace Asterisk.Sdk.Agi.Server;

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
    public async ValueTask SendCommandAsync(string command, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetByteCount(command) + 1; // +1 for \n
        var span = _writer.GetSpan(bytes);

        var written = Encoding.UTF8.GetBytes(command, span);
        span[written] = (byte)'\n';

        _writer.Advance(written + 1);
        await _writer.FlushAsync(ct);
    }
}
