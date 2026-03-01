using System.IO.Pipelines;

namespace Asterisk.NetAot.Ami.Internal;

/// <summary>
/// Serializes AMI actions to the wire protocol format using PipeWriter.
/// Uses source-generated serializers for AOT compatibility.
/// </summary>
public sealed class AmiProtocolWriter
{
    private readonly PipeWriter _writer;

    public AmiProtocolWriter(PipeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Writes an AMI action as key-value text followed by a blank line.
    /// </summary>
    public async ValueTask WriteActionAsync(Dictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        // TODO: Write each field as "Key: Value\r\n" then "\r\n" terminator
        // Use Span<byte> and PipeWriter.GetSpan() for zero-copy writes
        throw new NotImplementedException();
    }
}
