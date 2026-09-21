using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Pins the two figures in <c>README.md</c>'s "Status" block that are counts of this
/// repository's own contents, and are therefore ENFORCING however inconvenient
/// (ADR-0042 D1, <c>docs/claim-registry.md</c> rows 61 and 74).
///
/// Both had rotted before this guard existed: the headline read v2.2.1 against a tagged
/// v2.5.3, and "37 ADRs" against 53 files on disk. Neither is a performance figure, so
/// <see cref="PerformanceTableCoherenceTests"/> never looked at them, and nothing else did
/// either — a release bumps the version, an ADR lands, and the README goes quietly stale.
///
/// When either number legitimately moves, the test fails and the README is updated in the
/// same PR. That reverse coupling is the whole point.
///
/// A third case guards the decision catalog itself, which nothing did before it: the ADR count
/// below counts the *files* and excludes <c>docs/decisions/README.md</c> by name, so an ADR that
/// lands without gaining its row in that catalog passes it green.
/// </summary>
public sealed class StatusBlockCoherenceTests
{
    [Fact]
    public void TheHeadlineVersion_ShouldMatchTheVersionThePackagesShipWith()
    {
        var published = Regex.Match(ReadReadme(), @"^\*\*v(?<v>\d+\.\d+\.\d+)\*\* — ",
            RegexOptions.Multiline);
        published.Success.Should().BeTrue(
            "README.md's Status block must still open with a **vX.Y.Z** headline");

        var shipped = Regex.Match(ReadBuildProps(), @"<PackageVersion>(?<v>[^<]+)</PackageVersion>");
        shipped.Success.Should().BeTrue("Directory.Build.props must still declare <PackageVersion>");

        published.Groups["v"].Value.Should().Be(shipped.Groups["v"].Value,
            "the README headline is a claim about the version that ships; bump both in the same PR");
    }

    [Fact]
    public void ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk()
    {
        var published = Regex.Match(ReadReadme(), @"\*\*(?<n>\d+) ADRs\*\*");
        published.Success.Should().BeTrue(
            "README.md must still publish an **N ADRs** figure");

        // The catalog README.md in that directory indexes the decisions; it is not one.
        var onDisk = Directory.EnumerateFiles(Path.Join(RepoRoot(), "docs", "decisions"), "*.md")
            .Count(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.Ordinal));

        int.Parse(published.Groups["n"].Value, CultureInfo.InvariantCulture).Should().Be(onDisk,
            "the ADR count is a count of this repository's own contents (ADR-0042 D1); "
            + "an ADR that lands without the README moving is exactly the drift this catches");
    }

    /// <summary>
    /// The hole <see cref="ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk"/> leaves: it counts
    /// the files in <c>docs/decisions/</c> and excludes the catalog by name, so an ADR that lands
    /// without its row in <c>docs/decisions/README.md</c> is invisible to it — which is how three
    /// sibling changes carried that step and a fourth did not.
    ///
    /// <b>Set equality, never a count.</b> A count of rows against a count of files is satisfied by
    /// one id listed twice while another is omitted, which is precisely the drift this exists to
    /// catch. Ids come from the catalog's link *targets*, not from prose: a row that mentions
    /// "ADR-0053" in its summary is not a row for ADR-0053.
    /// </summary>
    [Fact]
    public void TheDecisionCatalog_ShouldListEveryAdrOnDisk()
    {
        var decisions = Path.Join(RepoRoot(), "docs", "decisions");

        // The catalog README.md in that directory indexes the decisions; it is not one.
        var files = Directory.EnumerateFiles(decisions, "*.md")
            .Select(f => Path.GetFileName(f)!)
            .Where(name => !string.Equals(name, "README.md", StringComparison.Ordinal))
            .ToList();

        files.Should().NotBeEmpty(
            "docs/decisions/ must still hold the ADRs; an empty set here would make every "
            + "comparison below vacuously true");
        files.Should().OnlyContain(name => Regex.IsMatch(name, @"^\d{4}-"),
            "every decision file carries a sequential four-digit prefix (docs/decisions/README.md, "
            + "File convention); a name this guard cannot parse drops out of the comparison unseen");

        var onDisk = files
            .Select(name => name[..4])
            .ToHashSet(StringComparer.Ordinal);

        var linked = Regex.Matches(File.ReadAllText(Path.Join(decisions, "README.md")),
                @"\]\((?<id>\d{4})-[^)]*\.md\)")
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var drift = onDisk.Except(linked).Order(StringComparer.Ordinal)
                .Select(id => $"ADR-{id} is a file in docs/decisions/ that the catalog does not list")
            .Concat(linked.Except(onDisk).Order(StringComparer.Ordinal)
                .Select(id => $"ADR-{id} is listed in the catalog with no file behind it"))
            .ToList();

        drift.Should().BeEmpty(
            "the catalog in docs/decisions/README.md must name exactly the ADRs on disk — an ADR "
            + "lands with its row in the same pull request, and a superseded one keeps both");
    }

    private static string ReadReadme() => File.ReadAllText(Path.Join(RepoRoot(), "README.md"));

    private static string ReadBuildProps() =>
        File.ReadAllText(Path.Join(RepoRoot(), "Directory.Build.props"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Join(dir.FullName, "Verbara.Sdk.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Verbara.Sdk.slnx).");
    }
}
