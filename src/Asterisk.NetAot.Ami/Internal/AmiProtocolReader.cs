using System.Buffers;
using System.IO.Pipelines;

namespace Asterisk.NetAot.Ami.Internal;

/// <summary>
/// Parses AMI protocol messages (key: value pairs separated by blank lines)
/// from a PipeReader using zero-copy Span-based parsing.
/// </summary>
public sealed class AmiProtocolReader
{
    private readonly PipeReader _reader;
    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    public AmiProtocolReader(PipeReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Reads the next complete AMI message (terminated by blank line).
    /// Returns a dictionary of key-value pairs.
    /// </summary>
    public async ValueTask<Dictionary<string, string>?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Implement zero-copy line-by-line parsing
        // 1. Read from PipeReader until blank line (\r\n\r\n)
        // 2. Parse key: value pairs using Span<byte>
        // 3. Handle multiline responses (Response: Follows ... --END COMMAND--)
        // 4. Handle duplicate keys (convert to list)
        throw new NotImplementedException();
    }
}
