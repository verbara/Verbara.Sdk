using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Asterisk.Sdk.Ami.Internal;

/// <summary>
/// Parses AMI protocol messages from a PipeReader.
/// AMI protocol format:
///   Key: Value\r\n
///   Key: Value\r\n
///   \r\n              (blank line = message boundary)
///
/// Special cases:
///   - "Response: Follows" → multiline body until "--END COMMAND--"
///   - Duplicate keys → last value wins (consistent with asterisk-java)
///   - Protocol identifier line: "Asterisk Call Manager/X.Y.Z"
/// </summary>
public sealed class AmiProtocolReader
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] EndCommand = "--END COMMAND--"u8.ToArray();

    private readonly PipeReader _reader;

    public AmiProtocolReader(PipeReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Reads the next complete AMI message.
    /// Returns null when the connection is closed (pipe completed).
    /// </summary>
    public async ValueTask<AmiMessage?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool isCommandResponse = false;
        StringBuilder? commandOutput = null;

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var line))
            {
                if (line.Length == 0)
                {
                    // Blank line = message boundary
                    if (fields.Count > 0)
                    {
                        if (commandOutput is not null)
                        {
                            fields["__CommandOutput"] = commandOutput.ToString();
                        }

                        _reader.AdvanceTo(buffer.Start);
                        return new AmiMessage(fields);
                    }

                    // Skip leading blank lines
                    continue;
                }

                var lineStr = GetString(line);

                // Handle multiline command response body (after all headers are parsed)
                if (isCommandResponse)
                {
                    if (lineStr.StartsWith("--END COMMAND--", StringComparison.Ordinal))
                    {
                        isCommandResponse = false;
                    }
                    else
                    {
                        commandOutput ??= new StringBuilder();
                        commandOutput.AppendLine(lineStr);
                    }

                    continue;
                }

                // Try to parse as "Key: Value"
                var colonIndex = lineStr.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = lineStr[..colonIndex].Trim();
                    var value = lineStr[(colonIndex + 1)..].Trim();
                    fields[key] = value;

                    // Detect "Response: Follows" — headers continue normally,
                    // command output starts when we see a non-header line
                    if (key.Equals("Response", StringComparison.OrdinalIgnoreCase)
                        && value.Equals("Follows", StringComparison.OrdinalIgnoreCase))
                    {
                        commandOutput = new StringBuilder();
                    }
                }
                else if (commandOutput is not null)
                {
                    // Non-header line in a Follows response — this is command output
                    isCommandResponse = true;
                    if (lineStr.StartsWith("--END COMMAND--", StringComparison.Ordinal))
                    {
                        isCommandResponse = false;
                    }
                    else
                    {
                        commandOutput.AppendLine(lineStr);
                    }
                }
                else
                {
                    // Protocol identifier or unparseable line
                    if (lineStr.Contains("Asterisk Call Manager", StringComparison.OrdinalIgnoreCase)
                        || lineStr.Contains("OpenPBX Call Manager", StringComparison.OrdinalIgnoreCase))
                    {
                        fields["__ProtocolIdentifier"] = lineStr;
                        _reader.AdvanceTo(buffer.Start);
                        return new AmiMessage(fields);
                    }
                }
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                // Connection closed; return remaining fields if any
                if (fields.Count > 0)
                {
                    return new AmiMessage(fields);
                }

                return null;
            }
        }
    }

    /// <summary>
    /// Try to read a single line (terminated by \r\n) from the buffer.
    /// Advances the buffer past the consumed line including the \r\n.
    /// </summary>
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, CrLf.AsSpan()))
        {
            line = lineBytes;
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        line = default;
        return false;
    }

    private static string GetString(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(sequence.FirstSpan);
        }

        return Encoding.UTF8.GetString(sequence.ToArray());
    }
}

/// <summary>
/// Represents a parsed AMI protocol message (event, response, or protocol identifier).
/// </summary>
public sealed class AmiMessage
{
    private readonly Dictionary<string, string> _fields;

    public AmiMessage(Dictionary<string, string> fields)
    {
        _fields = fields;
    }

    /// <summary>Get a field value by key (case-insensitive). Returns null if not found.</summary>
    public string? this[string key] =>
        _fields.TryGetValue(key, out var value) ? value : null;

    /// <summary>Whether this message contains the given key.</summary>
    public bool ContainsKey(string key) => _fields.ContainsKey(key);

    /// <summary>All field keys in this message.</summary>
    public IEnumerable<string> Keys => _fields.Keys;

    /// <summary>All fields as a read-only dictionary.</summary>
    public IReadOnlyDictionary<string, string> Fields => _fields;

    /// <summary>True if this is a protocol identifier message.</summary>
    public bool IsProtocolIdentifier => _fields.ContainsKey("__ProtocolIdentifier");

    /// <summary>True if this is an event message.</summary>
    public bool IsEvent => _fields.ContainsKey("Event");

    /// <summary>True if this is a response message.</summary>
    public bool IsResponse => _fields.ContainsKey("Response");

    /// <summary>The event type name, or null if not an event.</summary>
    public string? EventType => this["Event"];

    /// <summary>The response status ("Success", "Error", "Follows"), or null.</summary>
    public string? ResponseStatus => this["Response"];

    /// <summary>The ActionID field for correlating responses to actions.</summary>
    public string? ActionId => this["ActionID"];

    /// <summary>Command output for "Response: Follows" messages.</summary>
    public string? CommandOutput => this["__CommandOutput"];

    /// <summary>Protocol identifier string (e.g., "Asterisk Call Manager/6.0.0").</summary>
    public string? ProtocolIdentifier => this["__ProtocolIdentifier"];
}
