namespace Asterisk.NetAot.Agi.Server;

/// <summary>
/// Represents an AGI reply from Asterisk.
/// Format: "code result=value [extra]"
/// Example: "200 result=0" or "200 result=1 (speech)" or "510 Invalid command"
/// </summary>
public sealed class AgiReply
{
    /// <summary>AGI status code (200=success, 510=invalid, 520=usage).</summary>
    public int StatusCode { get; init; }

    /// <summary>The result value after "result=".</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>Extra data in parentheses, if any.</summary>
    public string? Extra { get; init; }

    /// <summary>The full raw reply line.</summary>
    public string RawLine { get; init; } = string.Empty;

    /// <summary>Whether this is a success reply (code 200).</summary>
    public bool IsSuccess => StatusCode == 200;

    /// <summary>The result as an integer, or -1 if not numeric.</summary>
    public int ResultAsInt => int.TryParse(Result, out var v) ? v : -1;

    /// <summary>The result as a char (for digit commands), or '\0'.</summary>
    public char ResultAsChar => Result.Length == 1 ? Result[0] : (ResultAsInt > 0 ? (char)ResultAsInt : '\0');

    /// <summary>Parse a raw AGI reply line.</summary>
    public static AgiReply Parse(string line)
    {
        // Format: "code result=value (extra)" or "code message"
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return new AgiReply { StatusCode = int.TryParse(line, out var c) ? c : -1, RawLine = line };
        }

        var code = int.TryParse(line.AsSpan(0, spaceIdx), out var statusCode) ? statusCode : -1;
        var rest = line[(spaceIdx + 1)..];

        var result = string.Empty;
        string? extra = null;

        var resultIdx = rest.IndexOf("result=", StringComparison.Ordinal);
        if (resultIdx >= 0)
        {
            var valueStart = resultIdx + 7;
            var valueEnd = rest.IndexOf(' ', valueStart);
            result = valueEnd >= 0 ? rest[valueStart..valueEnd] : rest[valueStart..];

            // Check for extra in parentheses
            var parenStart = rest.IndexOf('(', valueStart);
            if (parenStart >= 0)
            {
                var parenEnd = rest.IndexOf(')', parenStart);
                extra = parenEnd >= 0 ? rest[(parenStart + 1)..parenEnd] : rest[(parenStart + 1)..];
            }
        }

        return new AgiReply { StatusCode = code, Result = result, Extra = extra, RawLine = line };
    }
}
