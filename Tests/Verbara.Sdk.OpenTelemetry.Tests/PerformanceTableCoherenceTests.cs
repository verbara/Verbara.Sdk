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
                row.Cells.Should().Contain(value.GetString()!,
                    $"README.md's '{operation}' row must publish the recorded {field}; " +
                    "if the measurement changed, the record moves first, in its own reviewed commit");
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
    /// A deferral is only honest while it names what blocks it. This keeps the Postgres row's
    /// "the record measures Dapper, which no longer ships" from decaying into a bare omission.
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
        var readme = ReadReadme();
        var section = readme[readme.IndexOf("## Performance", StringComparison.Ordinal)..];
        var end = section.IndexOf("\n## ", StringComparison.Ordinal);
        if (end > 0) section = section[..end];

        foreach (Match m in Regex.Matches(section, @"^\|\s*(?<op>[^|]+?)\s*\|(?<rest>.+)\|\s*$",
                     RegexOptions.Multiline))
        {
            var op = m.Groups["op"].Value.Trim();
            if (op is "Operation" || op.StartsWith("---", StringComparison.Ordinal)) continue;
            yield return new Row(op, m.Groups["rest"].Value);
        }
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
