using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Asterisk.NetAot.Agi.Server;

/// <summary>
/// Reads AGI protocol data from a PipeReader.
/// AGI uses simple line-based text protocol (lines terminated by \n).
/// </summary>
public sealed class FastAgiReader
{
    private static readonly byte[] Lf = "\n"u8.ToArray();
    private readonly PipeReader _reader;

    public FastAgiReader(PipeReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Read the AGI request header (agi_key: value lines until blank line).
    /// </summary>
    public async ValueTask<AgiRequest> ReadRequestAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();

        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line is null || line.Length == 0)
            {
                break;
            }

            lines.Add(line);
        }

        return AgiRequest.Parse(lines);
    }

    /// <summary>
    /// Read a single AGI reply line from Asterisk.
    /// </summary>
    public async ValueTask<AgiReply?> ReadReplyAsync(CancellationToken ct = default)
    {
        var line = await ReadLineAsync(ct);
        return line is not null ? AgiReply.Parse(line) : null;
    }

    /// <summary>Read a single line (terminated by \n, with optional \r stripped).</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(ct);
            var buffer = result.Buffer;

            if (TryReadLine(ref buffer, out var line))
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                return line;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string? line)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, (byte)'\n'))
        {
            // Strip \r if present
            var text = lineBytes.IsSingleSegment
                ? Encoding.UTF8.GetString(lineBytes.FirstSpan)
                : Encoding.UTF8.GetString(lineBytes.ToArray());

            line = text.TrimEnd('\r');
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        line = null;
        return false;
    }
}
