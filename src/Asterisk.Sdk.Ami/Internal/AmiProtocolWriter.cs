using System.IO.Pipelines;
using System.Text;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Serializes AMI actions to the wire protocol format using PipeWriter.
/// Format:
///   Action: ActionName\r\n
///   ActionID: id\r\n
///   Key: Value\r\n
///   \r\n
/// </summary>
public sealed class AmiProtocolWriter
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] ColonSpace = ": "u8.ToArray();

    private readonly PipeWriter _writer;

    public AmiProtocolWriter(PipeWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Write an AMI action as key-value text followed by a blank line terminator.
    /// </summary>
    public async ValueTask WriteActionAsync(string actionName, string actionId,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        CancellationToken cancellationToken = default)
    {
        WriteField("Action", actionName);
        WriteField("ActionID", actionId);

        if (fields is not null)
        {
            foreach (var field in fields)
            {
                WriteField(field.Key, field.Value);
            }
        }

        // Blank line terminates the message
        WriteBytes(CrLf);

        await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Write a raw dictionary of fields (used by source-generated serializers).
    /// The caller is responsible for including Action and ActionID fields.
    /// </summary>
    public async ValueTask WriteFieldsAsync(IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken = default)
    {
        foreach (var field in fields)
        {
            WriteField(field.Key, field.Value);
        }

        WriteBytes(CrLf);
        await _writer.FlushAsync(cancellationToken);
    }

    private void WriteField(string key, string value)
    {
        var keyBytes = Encoding.UTF8.GetByteCount(key);
        var valueBytes = Encoding.UTF8.GetByteCount(value);
        var totalLength = keyBytes + ColonSpace.Length + valueBytes + CrLf.Length;

        var span = _writer.GetSpan(totalLength);
        var written = 0;

        written += Encoding.UTF8.GetBytes(key, span[written..]);
        ColonSpace.CopyTo(span[written..]);
        written += ColonSpace.Length;
        written += Encoding.UTF8.GetBytes(value, span[written..]);
        CrLf.CopyTo(span[written..]);
        written += CrLf.Length;

        _writer.Advance(written);
    }

    private void WriteBytes(byte[] bytes)
    {
        var span = _writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _writer.Advance(bytes.Length);
    }
}
