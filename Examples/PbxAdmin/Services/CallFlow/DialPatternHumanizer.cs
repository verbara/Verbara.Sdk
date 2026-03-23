namespace PbxAdmin.Services.CallFlow;

/// <summary>
/// Translates Asterisk dial patterns (e.g. <c>_NXXNXXXXXX</c>) into
/// human-readable descriptions and generates example numbers.
/// </summary>
public static class DialPatternHumanizer
{
    /// <summary>
    /// Returns a human-readable description for an Asterisk dial pattern.
    /// </summary>
    public static string Describe(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        // Exact known patterns
        if (pattern == "911")
            return "Emergency 911";

        // Pattern-based matching (starts with '_')
        if (pattern.StartsWith('_'))
        {
            var body = pattern[1..];

            // Service codes: _N11
            if (body == "N11")
                return "Service code (N11)";

            // International: _011X.
            if (body.StartsWith("011", StringComparison.Ordinal))
                return "International (011 prefix)";

            // International: _00X.
            if (body.StartsWith("00", StringComparison.Ordinal))
                return "International (00 prefix)";

            // 11-digit NANP: _1NXXNXXXXXX
            if (body == "1NXXNXXXXXX")
                return "11-digit starting with 1";

            // 10-digit NANP: _NXXNXXXXXX
            if (body == "NXXNXXXXXX")
                return "10-digit (e.g. 2125551234)";

            // 7-digit local: _NXXXXXX
            if (body == "NXXXXXX")
                return "7-digit local";

            // Catch-all: _X. or _.
            if (body is "X." or ".")
                return "Any number (catch-all)";

            // Unknown pattern — return raw
            return pattern;
        }

        // All digits → exact match
        if (IsAllDigits(pattern))
            return $"Exact: {pattern}";

        // Fallback
        return pattern;
    }

    /// <summary>
    /// Generates an example number that would match the given dial pattern,
    /// or <c>null</c> if no example can be produced.
    /// </summary>
    public static string? Example(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        // Exact digits — the example is the number itself
        if (!pattern.StartsWith('_'))
            return IsAllDigits(pattern) ? pattern : null;

        var body = pattern[1..];
        var hasWildcard = body.EndsWith('.');
        var core = hasWildcard ? body[..^1] : body;

        // Expand each pattern character to a concrete digit
        var chars = new char[core.Length + (hasWildcard ? 4 : 0)];
        for (var i = 0; i < core.Length; i++)
        {
            chars[i] = ExpandPatternChar(core[i]);
        }

        // For variable-length patterns (ending with '.'), append extra digits
        if (hasWildcard)
        {
            for (var i = core.Length; i < chars.Length; i++)
            {
                chars[i] = (char)('1' + (i % 9));
            }
        }

        return new string(chars);
    }

    private static char ExpandPatternChar(char c) => c switch
    {
        'N' => '2',  // 2-9
        'X' => '5',  // 0-9
        'Z' => '1',  // 1-9
        _ when c is >= '0' and <= '9' => c,
        _ => '5',    // fallback for unknown pattern chars
    };

    private static bool IsAllDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return value.Length > 0;
    }
}
