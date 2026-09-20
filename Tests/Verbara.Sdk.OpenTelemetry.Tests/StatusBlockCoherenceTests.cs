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
