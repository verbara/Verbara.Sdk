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
///
/// String optimization: keys and common values are interned via AmiStringPool.
/// Field parsing uses span-based byte scanning to avoid intermediate string allocations.
/// </summary>
public sealed class AmiProtocolReader
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] EndCommandMarker = "--END COMMAND--"u8.ToArray();

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

                // Command response body: all lines are body until --END COMMAND--
                if (isCommandResponse)
                {
                    if (StartsWithEndCommand(line))
                    {
                        isCommandResponse = false;
                    }
                    else
                    {
                        commandOutput ??= new StringBuilder();
                        commandOutput.AppendLine(GetString(line));
                    }

                    continue;
                }

                // Fast path: parse field directly from bytes (no intermediate string allocation)
                if (TryParseFieldBytes(line, out var key, out var value))
                {
                    fields[key] = value;

                    if (commandOutput is null
                        && key.Equals("Response", StringComparison.OrdinalIgnoreCase)
                        && value.Equals("Follows", StringComparison.OrdinalIgnoreCase))
                    {
                        commandOutput = new StringBuilder();
                    }

                    continue;
                }

                // Slow path: no colon found — command output start or protocol identifier
                if (commandOutput is not null)
                {
                    // First non-header line after Response: Follows
                    isCommandResponse = true;
                    if (StartsWithEndCommand(line))
                    {
                        isCommandResponse = false;
                    }
                    else
                    {
                        commandOutput.AppendLine(GetString(line));
                    }
                }
                else
                {
                    // Protocol identifier or unparseable line
                    var lineStr = GetString(line);
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
    /// Parse a "Key: Value" field directly from bytes without materializing the full line string.
    /// Key is resolved via AmiStringPool (interned for known keys).
    /// Value is resolved via AmiStringPool (interned for common values).
    /// </summary>
    private static bool TryParseFieldBytes(ReadOnlySequence<byte> line, out string key, out string value)
    {
        if (line.IsSingleSegment)
            return ParseFieldSpan(line.FirstSpan, out key, out value);

        if (line.Length <= 512)
        {
            Span<byte> buf = stackalloc byte[(int)line.Length];
            line.CopyTo(buf);
            return ParseFieldSpan(buf, out key, out value);
        }

        key = default!;
        value = default!;
        return false;
    }

    private static bool ParseFieldSpan(ReadOnlySpan<byte> span, out string key, out string value)
    {
        const byte Colon = (byte)':';
        const byte Space = (byte)' ';

        var colonIdx = span.IndexOf(Colon);
        if (colonIdx <= 0)
        {
            key = default!;
            value = default!;
            return false;
        }

        // Extract key (before colon, trim trailing spaces)
        var keySpan = span[..colonIdx];
        while (keySpan.Length > 0 && keySpan[^1] == Space)
            keySpan = keySpan[..^1];

        // Extract value (after colon, trim leading/trailing spaces)
        var valueSpan = span[(colonIdx + 1)..];
        while (valueSpan.Length > 0 && valueSpan[0] == Space)
            valueSpan = valueSpan[1..];
        while (valueSpan.Length > 0 && valueSpan[^1] == Space)
            valueSpan = valueSpan[..^1];

        key = AmiStringPool.GetKey(keySpan);
        value = AmiStringPool.GetValue(valueSpan);
        return true;
    }

    /// <summary>
    /// Check if a line starts with "--END COMMAND--" using byte comparison.
    /// </summary>
    private static bool StartsWithEndCommand(ReadOnlySequence<byte> line)
    {
        if (line.Length < EndCommandMarker.Length)
            return false;

        if (line.IsSingleSegment)
            return line.FirstSpan.StartsWith(EndCommandMarker);

        return CheckEndCommandMultiSegment(line);
    }

    private static bool CheckEndCommandMultiSegment(ReadOnlySequence<byte> line)
    {
        Span<byte> buf = stackalloc byte[EndCommandMarker.Length];
        line.Slice(0, EndCommandMarker.Length).CopyTo(buf);
        return buf.SequenceEqual(EndCommandMarker);
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
