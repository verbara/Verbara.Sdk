namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Applies the analyzer-config rule of <c>scripts/ci/check-public-api-guard-coverage.sh</c> to one file's text: every
/// occurrence of <c>dotnet_diagnostic.RS0016.severity</c> (any case) is <c>warning</c> or <c>error</c>, at least one
/// exists, and no key begins with <c>dotnet_public_api_analyzer</c> or <c>dotnet_analyzer_diagnostic</c>.
/// </summary>
/// <remarks>
/// Lines are read the way the compiler reads an analyzer-config file: comment (<c>#</c>, <c>;</c>) and section lines
/// are skipped, the key is lowercased, <c>=</c> or <c>:</c> separates it from the value, the value is cut at an inline
/// <c>#</c> or <c>;</c>, and a trailing carriage return is stripped. The guard script runs the same rule over every
/// analyzer-config file a compile was handed; this copy lets the unit lane fail on the root file before any build has
/// left a record.
/// </remarks>
internal static class PublicApiSeverityScanner
{
    public const string SeverityKey = "dotnet_diagnostic.rs0016.severity";

    private static readonly string[] BannedPrefixes = ["dotnet_public_api_analyzer", "dotnet_analyzer_diagnostic"];

    /// <summary>The outcome of a scan: how many severity lines were found, and every violation as a line of text.</summary>
    public sealed record Report(int SeverityLines, IReadOnlyList<string> Violations)
    {
        public bool IsClean => Violations.Count == 0;

        public override string ToString() => IsClean
            ? $"clean: {SeverityLines} RS0016 severity line(s)"
            : string.Join(Environment.NewLine, Violations);
    }

    /// <summary>Scans the text of one analyzer-config file, reported as <paramref name="path"/>.</summary>
    public static Report Scan(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(text);

        var severityLines = 0;
        var violations = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var line = raw.TrimStart(' ', '\t');
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
                continue;

            var separator = line.IndexOfAny(['=', ':']);
            if (separator < 0)
                continue;

            var key = line[..separator].TrimEnd(' ', '\t').ToLowerInvariant();
            var value = line[(separator + 1)..];
            var comment = value.IndexOfAny(['#', ';']);
            if (comment >= 0)
                value = value[..comment];
            value = value.Trim(' ', '\t').ToLowerInvariant();

            var where = $"{path}:{i + 1}: {raw}";
            if (BannedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                violations.Add($"{where} — a key under '{BannedPrefixes.First(prefix => key.StartsWith(prefix, StringComparison.Ordinal))}' is banned");
            }
            else if (key == SeverityKey)
            {
                severityLines++;
                if (value is not ("warning" or "error"))
                    violations.Add($"{where} — RS0016 severity '{value}' is neither warning nor error");
            }
        }

        if (severityLines == 0)
            violations.Add($"{path}: no dotnet_diagnostic.RS0016.severity line");

        return new Report(severityLines, violations);
    }
}
