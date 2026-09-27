using System.Text.RegularExpressions;
using FluentAssertions;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Pins the figure in <c>README.md</c>'s "Status" block that is a fact about this
/// repository's own contents, and is therefore ENFORCING however inconvenient
/// (ADR-0042 D1, <c>docs/claim-registry.md</c> row 61).
///
/// It had rotted before this guard existed: the headline read v2.2.1 against a tagged
/// v2.5.3. It is not a performance figure, so <see cref="PerformanceTableCoherenceTests"/>
/// never looked at it, and nothing else did either — a release bumps the version and the
/// README goes quietly stale.
///
/// When the number legitimately moves, the test fails and the README is updated in the
/// same PR. That reverse coupling is the whole point.
///
/// The ADR-count figure this class once pinned is gone from the README together with the
/// public decision catalog it counted; architecture decision records are kept locally and
/// are no longer tracked here.
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
