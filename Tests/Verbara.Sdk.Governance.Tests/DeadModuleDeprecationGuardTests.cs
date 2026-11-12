using Xunit.Abstractions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The surface of dead Asterisk modules that 2.9.0 deprecated with one id stays removed: none of its 49 types and one
/// member is declared again in the package that declared it, nothing in source or build configuration uses the retired
/// id, and the migration guide keeps the one heading that the 2.9.0 binaries' warning links open.
/// </summary>
/// <remarks>
/// These controls run the family guard, <see cref="DeprecationIdScanner"/>, under a register holding only the retired
/// <c>VSDK0005</c> row, so each check of the removal reads as it did before the family guard took it over. The first
/// case reads the real tree under the real register, so it holds the whole family, not only <c>VSDK0005</c>. The
/// others build small fixture trees, so each failure the guard exists to catch is shown to turn it red. Attributes and
/// pragmas in the fixtures are assembled from parts so this file never holds one the real scan could read.
/// </remarks>
public sealed class DeadModuleDeprecationGuardTests(ITestOutputHelper output)
{
    private const string Id = "VSDK0005";
    private const string GuidePath = DeprecationIdRegister.RemovalGuide2_9;
    private static readonly string Pragma = "#" + "pragma warning";

    private static readonly RemovedSymbol RemovedType = new("Pkg", "GoneAction");
    private static readonly RemovedSymbol RemovedMember = new("Pkg", "Activity.UseOld");

    [Fact]
    public void Tree_ShouldHoldNoneOfTheRemovedSetAndNoUseOfTheRetiredId_WhenScanned()
    {
        var report = DeprecationIdScanner.Scan(RepoRoot(), DeprecationIdRegister.Rows);
        output.WriteLine(report.ToString());

        report.SourceFiles.Should().BeGreaterThan(0, "a guard that read no source would pass on nothing");
        report.IsClean.Should().BeTrue(
            "none of the 49 types and the member is declared again; nothing in src/, Tests/ or the build configuration " +
            $"uses \"{Id}\"; the guide holds one heading for it; and every other id of DeprecationIdRegister holds. Found:" +
            Environment.NewLine + report);
    }

    [Fact]
    public void RemovedSet_ShouldHoldFortyNineTypesAndOneMember_WhenRead()
    {
        var set = DeprecationIdRegister.Row(Id).RemovedSet;

        set.Should().HaveCount(50).And.OnlyHaveUniqueItems();
        set.Count(s => s.Key.Contains('.', StringComparison.Ordinal)).Should().Be(1, "one member: MeetmeActivity.UseConfBridge");
        set.Count(s => s.Package == "Verbara.Sdk.Ami").Should().Be(48);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheTreeHoldsNoneOfTheRemovedSet()
    {
        using var tree = GreenTree();

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldFail_WhenNoSourceFileIsFound()
    {
        using var tree = new FixtureTree();
        tree.Write(GuidePath, "# Guide\n\n## " + Id + "\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([]));

        report.Violations.Should().Contain(v => v.StartsWith("found no source file under src/", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldNameTheType_WhenARemovedTypeIsDeclaredAgainAnywhereInItsPackage()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Elsewhere/Gone.cs", "namespace Pkg;\n\npublic sealed class GoneAction { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Elsewhere/Gone.cs:3: GoneAction is declared again; it was removed in 2.10.0");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnotherPackageDeclaresATypeOfTheSameName()
    {
        using var tree = GreenTree();
        tree.Write("src/Other/Gone.cs", "namespace Other;\n\npublic sealed class GoneAction { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldNameTheMember_WhenTheRemovedMemberIsDeclaredAgain()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Activity.cs",
            "namespace Pkg;\n\npublic sealed class Activity\n{\n    public bool UseOld { get; init; } = true;\n}\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Activity.cs:5: Activity.UseOld is declared again; it was removed in 2.10.0");
    }

    [Theory]
    [InlineData("src/Pkg/Alive.cs")]
    [InlineData("Tests/Some/AliveTests.cs")]
    public void Scan_ShouldNameTheLine_WhenAnObsoleteMarkCarriesTheRetiredId(string path)
    {
        using var tree = GreenTree();
        tree.Write(path, "namespace Pkg;\n\n[System.Obsolete(\"m\", DiagnosticId = \"" + Id + "\")]\npublic sealed class AliveAction { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v == $"{path}:3: an [Obsolete] carries the retired id {Id}");
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("restore")]
    public void Scan_ShouldNameTheLine_WhenAPragmaNamesTheRetiredId(string verb)
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/SiteTests.cs",
            "namespace Pkg;\n\npublic static class Site\n{\n" + Pragma + " " + verb + " " + Id + " // a reason\n    public static object Make() => new object();\n}\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v == $"Tests/Some/SiteTests.cs:5: a #pragma warning names the retired id {Id}");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheIdIsOnlyTextInAStringLiteral()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/TextTests.cs",
            "namespace Pkg;\n\npublic static class Text\n{\n    public const string Id = \"" + Id + "\";\n}\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldNameTheFile_WhenAnAssemblyAttributeSuppressesTheId()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/GlobalSuppressions.cs",
            "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"" + Id + "\", Justification = \"x\")]\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v == $"Tests/Some/GlobalSuppressions.cs:1: an assembly-wide SuppressMessage names the retired id {Id}");
    }

    [Theory]
    [InlineData("Tests/Some/Suite.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);" + Id + "</NoWarn></PropertyGroup></Project>\n")]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup>\n<WarningsNotAsErrors>" + Id + "</WarningsNotAsErrors></PropertyGroup></Project>\n")]
    [InlineData("src/Pkg/Pkg.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" NoWarn=\"" + Id + "\" /></ItemGroup></Project>\n")]
    [InlineData(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic." + Id + ".severity = none\n")]
    [InlineData("Tests/.globalconfig", "is_global = true\ndotnet_diagnostic." + Id + ".severity = suggestion\n")]
    [InlineData("Directory.Build.rsp", "-nowarn:" + Id + "\n")]
    public void Scan_ShouldNameTheFile_WhenBuildConfigurationNamesTheId(string path, string text)
    {
        using var tree = GreenTree();
        tree.Write(path, text);

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v.StartsWith(path + ":", StringComparison.Ordinal)
            && v.EndsWith($"build configuration names the retired id {Id}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Guide\n", 0)]
    [InlineData("# Guide\n\n## " + Id + "\n\n## " + Id + "\n", 2)]
    [InlineData("# Guide\n\n## " + Id + "\n\n### " + Id + "\n", 2)]
    [InlineData("# Guide\n\n## Removed in 2.10.0 (" + Id + ")\n", 0)]
    [InlineData("# Guide\n\n```md\n## " + Id + "\n```\n", 0)]
    public void Scan_ShouldFail_WhenTheGuideDoesNotHoldExactlyOneHeadingForTheId(string guide, int headings)
    {
        using var tree = GreenTree();
        tree.Write(GuidePath, guide);

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.Violations.Should().ContainSingle(v => v.Contains($"holds {headings} headings whose text is \"{Id}\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Guide\n\n### " + Id + "\n")]
    [InlineData("# Guide\n\n```md\n## " + Id + "\n```\n\n## " + Id + "\n")]
    public void Scan_ShouldPass_WhenTheGuideHoldsOneHeadingForTheIdAtAnyLevelOutsideCode(string guide)
    {
        using var tree = GreenTree();
        tree.Write(GuidePath, guide);

        var report = DeprecationIdScanner.Scan(tree.Root, Register([RemovedType, RemovedMember]));

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Theory]
    [InlineData("VSDK0005", "vsdk0005")]
    [InlineData("Deprecated in 2.9.0 (VSDK0005)", "deprecated-in-290-vsdk0005")]
    public void Slug_ShouldMatchGitHubsAnchor_WhenGivenAHeading(string heading, string slug)
    {
        DeprecationIdScanner.Slug(heading).Should().Be(slug);
    }

    [Fact]
    public void UrlFormat_ShouldEndInTheSlugOfTheGuideHeading_WhenRead()
    {
        DeprecationIdRegister.Row(Id).Link.Should().EndWith("#" + DeprecationIdScanner.Slug(Id))
            .And.NotContain("{0}", "one anchor serves the whole id");
    }

    /// <summary>A fixture register holding only the retired id, with its guide and the given removed set.</summary>
    private static IReadOnlyList<DeprecationIdRow> Register(IReadOnlyList<RemovedSymbol> removedSet) =>
        [new(Id, DeprecationIdState.Retired, "2.10.0", GuidePath, [], removedSet)];

    private static FixtureTree GreenTree()
    {
        var tree = new FixtureTree();
        tree.Write("src/Pkg/Kept.cs", "namespace Pkg;\n\npublic sealed class KeptAction { }\n");
        tree.Write("src/Pkg/Activity.cs", "namespace Pkg;\n\npublic sealed class Activity\n{\n    public string? Room { get; init; }\n}\n");
        tree.Write("src/Pkg/Pkg.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        tree.Write(GuidePath, "# Guide\n\n## " + Id + "\n\nDeprecated in 2.9.0, removed in 2.10.0.\n");
        return tree;
    }

    private static string RepoRoot() => Path.GetDirectoryName(TestTreeSource.TestsRoot())!;

    private sealed class FixtureTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("dead-module-").FullName;

        public void Write(string relative, string text)
        {
            var path = Path.Join(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
