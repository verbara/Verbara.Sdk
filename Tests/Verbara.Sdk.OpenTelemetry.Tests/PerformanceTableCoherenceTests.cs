using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Binds every absolute figure in <c>README.md</c>'s Performance table to
/// <c>docs/research/performance-record.json</c> (ADR-0042 D7). The table asserts "this is what we
/// measured, on this machine, on this date, and you can reproduce it"; this fails the build when
/// the document and the record stop agreeing.
/// </summary>
/// <remarks>
/// <para>
/// Rides the existing <c>Unit Tests</c> job beside <c>MarketingClaimsTests</c> — no new job and no
/// new check-run name, because a guard that costs a branch-protection edit is not cheap whatever its
/// runtime (ADR-0042 D3).
/// </para>
/// <para>
/// <b>This is not the regression gate.</b> Correspondence between document and record is this test's
/// job; regression protection is <c>Tests/Verbara.Sdk.Benchmarks/baseline.json</c>, measured on
/// hosted runners that are 2× slower. They are separate failures and are detected separately.
/// </para>
/// </remarks>
public sealed class PerformanceTableCoherenceTests
{
    private sealed record Row(string Operation, string Cells);

    [Fact]
    public void EveryPublishedFigure_ShouldMatchTheCommittedMeasurementRecord()
    {
        var record = LoadRecord();
        var bound = record.RootElement.GetProperty("rows");

        foreach (var entry in bound.EnumerateArray())
        {
            var operation = entry.GetProperty("operation").GetString()!;
            var row = FindRow(operation);

            foreach (var field in new[] { "latency", "throughput", "batch", "versus_v1_0" })
            {
                if (!entry.TryGetProperty(field, out var value)) continue;

                var figure = value.GetString()!;
                row.Cells.Should().MatchRegex(WholeFigure(figure),
                    $"README.md's '{operation}' row must publish the recorded {field} ('{figure}') as a " +
                    "whole figure; if the measurement changed, the record moves first, in its own reviewed commit");
            }
        }
    }

    /// <summary>
    /// The hole this closes: without it a new Performance row ships unbound and the test above still
    /// passes, because it only walks the record. A figure with no record entry is exactly the thing
    /// this capability exists to refuse.
    /// </summary>
    [Fact]
    public void EveryTableRow_ShouldBeEitherBoundOrExplicitlyDeferred()
    {
        var record = LoadRecord();
        var accounted = record.RootElement.GetProperty("rows").EnumerateArray()
            .Concat(record.RootElement.GetProperty("deferred_rows").EnumerateArray())
            .Select(e => e.GetProperty("operation").GetString()!)
            .ToHashSet();

        var published = ReadTableRows().Select(r => r.Operation).ToList();

        published.Should().NotBeEmpty("the Performance table must still be findable in README.md");
        published.Should().OnlyContain(op => accounted.Contains(op),
            "every published figure carries a record entry or a declared deferral (ADR-0042 D1) — " +
            "add the row to docs/research/performance-record.json in the same pull request");
    }

    /// <summary>
    /// A deferral is only honest while it names what blocks it; this keeps one from decaying into a
    /// bare omission. <c>deferred_rows</c> is empty today. Its one historical case was the Postgres
    /// session-store row, deferred because its record measured the store on Dapper after Dapper had
    /// stopped shipping, and bound once the store was re-measured. The test stays for the next one.
    /// </summary>
    [Fact]
    public void EveryDeferredRow_ShouldNameItsBlockerAndItsUnblockingCondition()
    {
        var record = LoadRecord();

        foreach (var entry in record.RootElement.GetProperty("deferred_rows").EnumerateArray())
        {
            var operation = entry.GetProperty("operation").GetString()!;
            entry.TryGetProperty("why_not_bound", out var why).Should().BeTrue(
                $"'{operation}' is deferred and must say why");
            entry.TryGetProperty("unblocking_condition", out var how).Should().BeTrue(
                $"'{operation}' is deferred and must say what would unblock it");
            why.GetString().Should().NotBeNullOrWhiteSpace();
            how.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void TheRecordsProvenance_ShouldMatchTheHeaderTheTablePublishes()
    {
        var record = LoadRecord();
        var measurement = record.RootElement.GetProperty("measurement");
        var header = ReadReadme();

        foreach (var field in new[] { "machine", "runtime", "benchmarkdotnet", "date" })
        {
            header.Should().Contain(measurement.GetProperty(field).GetString()!,
                $"README.md's Performance header must state the recorded {field} — " +
                "a figure without its measurement conditions is not reproducible");
        }
    }

    /// <summary>
    /// The header test above cannot see a row that was measured apart from the header: the table
    /// could keep crediting BenchmarkDotNet on the header's date for figures a <c>Stopwatch</c> took
    /// months later. A row measured apart from the header declares its own <c>provenance</c> in the
    /// record, and its date and runtime must be stated in the Performance section itself — not
    /// anywhere else in README.md, where a matching string would say nothing about the table. A row
    /// that declares no <c>provenance</c> is not checked here: deleting the object from the record
    /// hands the row back to the header unnoticed.
    /// </summary>
    [Fact]
    public void EveryRowWithItsOwnProvenance_ShouldHaveItStatedInThePerformanceSection()
    {
        var record = LoadRecord();
        var section = ReadPerformanceSection();

        foreach (var entry in record.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (!entry.TryGetProperty("provenance", out var provenance)) continue;
            var operation = entry.GetProperty("operation").GetString()!;

            foreach (var field in new[] { "date", "runtime" })
            {
                provenance.TryGetProperty(field, out var value).Should().BeTrue(
                    $"'{operation}' carries its own provenance, which must record its {field}");
                section.Should().Contain(value.GetString()!,
                    $"README.md's Performance section must state the {field} of '{operation}', " +
                    "which was measured apart from the table's header");
            }
        }
    }

    /// <summary>
    /// <c>docs/guides/session-store-backends.md</c> publishes the same two session-store measurements
    /// as README.md, to the reader choosing a backend, and published different figures for them —
    /// ~250 µs for a Redis save the record has at 30 µs — because nothing bound the guide. Its
    /// <c>## Benchmarks</c> section must now carry each session-store row's latency and batch as whole
    /// figures, and that row's date and runtime. A record with no session-store row fails rather than
    /// passing on an empty loop.
    /// </summary>
    [Fact]
    public void EverySessionStoreRow_ShouldHaveItsFiguresAndProvenanceStatedInTheGuidesBenchmarksSection()
    {
        var record = LoadRecord();
        var section = ReadGuideBenchmarksSection();

        var sessionRows = record.RootElement.GetProperty("rows").EnumerateArray()
            .Where(e => e.GetProperty("operation").GetString()!.StartsWith("Session store", StringComparison.Ordinal))
            .ToList();

        sessionRows.Should().NotBeEmpty(
            "the record must still hold the session-store rows the guide's Benchmarks section publishes — " +
            "with none, this test would pass while binding nothing");

        foreach (var entry in sessionRows)
        {
            var operation = entry.GetProperty("operation").GetString()!;
            entry.TryGetProperty("provenance", out var provenance).Should().BeTrue(
                $"'{operation}' was measured apart from README.md's table header and must record its provenance");

            var required = new[]
            {
                ("latency", entry), ("batch", entry), ("date", provenance), ("runtime", provenance),
            };

            foreach (var (field, source) in required)
            {
                source.TryGetProperty(field, out var value).Should().BeTrue(
                    $"'{operation}' must record its {field}");
                var figure = value.GetString()!;
                section.Should().MatchRegex(WholeFigure(figure),
                    $"docs/guides/session-store-backends.md's Benchmarks section must state the recorded {field} " +
                    $"of '{operation}' ('{figure}') as a whole figure; if the measurement changed, the record " +
                    "moves first, in its own reviewed commit");
            }
        }
    }

    /// <summary>
    /// A whole figure, not a substring: "11.62M events/sec" contains "1.62M events/sec", so a plain
    /// Contains would pass a 7x overclaim. No digit, dot or comma may touch it.
    /// </summary>
    private static string WholeFigure(string figure) => $@"(?<![\d.,]){Regex.Escape(figure)}(?!\d)";

    private static Row FindRow(string operation)
    {
        var row = ReadTableRows().FirstOrDefault(r => r.Operation == operation);
        row.Should().NotBeNull(
            $"the record lists '{operation}' but README.md's Performance table has no such row — " +
            "a record entry outliving its claim is as stale as the reverse");
        return row!;
    }

    private static IEnumerable<Row> ReadTableRows()
    {
        var section = ReadPerformanceSection();

        foreach (Match m in Regex.Matches(section, @"^\|\s*(?<op>[^|]+?)\s*\|(?<rest>.+)\|\s*$",
                     RegexOptions.Multiline))
        {
            var op = m.Groups["op"].Value.Trim();
            if (op is "Operation" || op.StartsWith("---", StringComparison.Ordinal)) continue;
            yield return new Row(op, m.Groups["rest"].Value);
        }
    }

    private static string ReadPerformanceSection() => SectionOf(ReadReadme(), "## Performance", "README.md");

    private static string ReadGuideBenchmarksSection() => SectionOf(
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "guides", "session-store-backends.md")),
        "## Benchmarks", "docs/guides/session-store-backends.md");

    private static string SectionOf(string markdown, string heading, string document)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{document} must still have its '{heading}' section");
        var section = markdown[start..];
        var end = section.IndexOf("\n## ", StringComparison.Ordinal);
        return end > 0 ? section[..end] : section;
    }

    private static string ReadReadme() => File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

    private static JsonDocument LoadRecord() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "research", "performance-record.json")));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Verbara.Sdk.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Verbara.Sdk.slnx).");
    }
}
