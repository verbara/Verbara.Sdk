using Xunit.Abstractions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The surface of dead Asterisk modules carries one deprecation id, as a warning, with one link, on exactly the ruled set
/// of 44 types and one member; nothing else carries it, nothing suppresses it project-wide, every site-local suppression
/// is restored in its file and gives its reason, and the migration guide holds the one section the link points at.
/// </summary>
/// <remarks>
/// The first case reads the real tree. The others build small fixture trees, so each failure the guard exists to catch
/// is shown to turn it red: no source at all, a listed file missing, an unlisted mark, a mark as an error. Attributes and
/// pragmas in the fixtures are assembled from parts so this file never holds one the real scan could read.
/// </remarks>
public sealed class DeadModuleDeprecationGuardTests(ITestOutputHelper output)
{
    private const string Id = DeadModuleDeprecationScanner.DiagnosticId;
    private static readonly string Url = DeadModuleDeprecationScanner.UrlFormat;
    private static readonly string Pragma = "#" + "pragma warning";

    private static readonly MarkedSymbol Listed = new("Pkg/Gone.cs", "GoneAction");

    [Fact]
    public void Tree_ShouldMarkExactlyTheRuledSet_WhenScanned()
    {
        var report = DeadModuleDeprecationScanner.Scan(RepoRoot(), DeadModuleDeprecationScanner.RuledSet);
        output.WriteLine(report.ToString());

        report.SourceFiles.Should().BeGreaterThan(0, "a guard that read no source would pass on nothing");
        report.IsClean.Should().BeTrue(
            $"each of the 44 types and the member carries [Obsolete] with DiagnosticId = \"{Id}\", as a warning, with " +
            "the guide's link; nothing else carries it; nothing suppresses it project-wide; every site-local " +
            "suppression is restored and gives its reason; the guide holds one section for it. Found:" +
            Environment.NewLine + report);
    }

    [Fact]
    public void RuledSet_ShouldHoldFortyFourTypesAndOneMember_WhenRead()
    {
        var set = DeadModuleDeprecationScanner.RuledSet;

        set.Should().HaveCount(45).And.OnlyHaveUniqueItems();
        set.Count(s => s.Key.Contains('.', StringComparison.Ordinal)).Should().Be(1, "one member: MeetmeActivity.UseConfBridge");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheListedSymbolIsMarkedAndTheGuideHoldsItsSection()
    {
        using var tree = GreenTree();

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.IsClean.Should().BeTrue(report.ToString());
        report.Marks.Should().ContainSingle(m => m.Key == "GoneAction");
    }

    [Fact]
    public void Scan_ShouldFail_WhenNoSourceFileIsFound()
    {
        using var tree = new FixtureTree();
        tree.Write(DeadModuleDeprecationScanner.GuidePath, "# Guide\n\n## " + Id + "\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, []);

        report.Violations.Should().Contain(v => v.StartsWith("found no source file under src/", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldFail_WhenAListedFileIsMissing()
    {
        using var tree = GreenTree();

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed, new("Pkg/Missing.cs", "MissingAction")]);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Missing.cs: listed file missing (for MissingAction)");
    }

    [Fact]
    public void Scan_ShouldNameTheSymbol_WhenAListedSymbolIsNotMarked()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Gone.cs", "namespace Pkg;\n\npublic sealed class GoneAction { }\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.StartsWith("src/Pkg/Gone.cs: GoneAction carries no [Obsolete]", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldNameTheSymbol_WhenAnUnlistedDeclarationCarriesTheId()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Alive.cs", "namespace Pkg;\n\n" + Mark() + "\npublic sealed class AliveAction { }\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v == $"src/Pkg/Alive.cs:3: AliveAction carries {Id} but is not in the ruled set");
    }

    [Theory]
    [InlineData(", true")]
    [InlineData(", error: true")]
    public void Scan_ShouldFail_WhenAListedSymbolCarriesTheIdAsAnError(string errorArgument)
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Gone.cs", "namespace Pkg;\n\n" + Mark(errorArgument) + "\npublic sealed class GoneAction { }\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.Contains($"GoneAction carries {Id} as an error", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldFail_WhenAListedSymbolLinksElsewhere()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Gone.cs",
            "namespace Pkg;\n\n[System.Obsolete(\"m\", DiagnosticId = \"" + Id + "\", UrlFormat = \"https://example.invalid/x\")]\npublic sealed class GoneAction { }\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.Contains("GoneAction has UrlFormat \"https://example.invalid/x\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReadAMemberMark_WhenItSitsOnAProperty()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Activity.cs",
            "namespace Pkg;\n\npublic sealed class Activity\n{\n    " + Mark() + "\n    public bool UseOld { get; init; }\n}\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed, new("Pkg/Activity.cs", "Activity.UseOld")]);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Theory]
    [InlineData("Tests/Some/Suite.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);" + Id + "</NoWarn></PropertyGroup></Project>\n")]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup>\n<WarningsNotAsErrors>" + Id + "</WarningsNotAsErrors></PropertyGroup></Project>\n")]
    [InlineData("src/Pkg/Pkg.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" NoWarn=\"" + Id + "\" /></ItemGroup></Project>\n")]
    [InlineData(".editorconfig", "root = true\n[*.cs]\ndotnet_diagnostic." + Id + ".severity = none\n")]
    [InlineData("Tests/.globalconfig", "is_global = true\ndotnet_diagnostic." + Id + ".severity = suggestion\n")]
    public void Scan_ShouldNameTheFile_WhenConfigurationSuppressesTheIdProjectWide(string path, string text)
    {
        using var tree = GreenTree();
        tree.Write(path, text);

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.StartsWith(path + ":", StringComparison.Ordinal)
            && v.EndsWith($"suppresses {Id} project-wide", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldNameTheFile_WhenAnAssemblyAttributeSuppressesTheId()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/GlobalSuppressions.cs",
            "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"" + Id + "\", Justification = \"x\")]\n");

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.StartsWith("Tests/Some/GlobalSuppressions.cs:1: [assembly: SuppressMessage]", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldPass_WhenASiteLocalSuppressionIsRestoredAndGivesItsReason()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/SiteTests.cs", Site($"{Pragma} disable {Id} // pins the deprecated surface until its removal", $"{Pragma} restore {Id}"));

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Theory]
    [InlineData("disable-no-restore", "has no matching restore in the same file")]
    [InlineData("disable-no-reason", "carries no trailing reason comment")]
    [InlineData("bare-disable", "a bare #pragma warning disable silences")]
    [InlineData("restore-only", "restores nothing")]
    public void Scan_ShouldNameTheLine_WhenASiteLocalSuppressionIsUnpairedOrUnexplained(string shape, string expected)
    {
        using var tree = GreenTree();
        var (open, close) = shape switch
        {
            "disable-no-restore" => ($"{Pragma} disable {Id} // reason", ""),
            "disable-no-reason" => ($"{Pragma} disable {Id}", $"{Pragma} restore {Id}"),
            "bare-disable" => ($"{Pragma} disable", $"{Pragma} restore"),
            _ => ("", $"{Pragma} restore {Id}"),
        };
        tree.Write("src/Pkg/Site.cs", Site(open, close));

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.StartsWith("src/Pkg/Site.cs:", StringComparison.Ordinal)
            && v.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Guide\n", 0)]
    [InlineData("# Guide\n\n## " + Id + "\n\n## " + Id + "\n", 2)]
    [InlineData("# Guide\n\n## Deprecated in 2.9.0 (" + Id + ")\n", 0)]
    public void Scan_ShouldFail_WhenTheGuideDoesNotHoldExactlyOneSectionForTheId(string guide, int headings)
    {
        using var tree = GreenTree();
        tree.Write(DeadModuleDeprecationScanner.GuidePath, guide);

        var report = DeadModuleDeprecationScanner.Scan(tree.Root, [Listed]);

        report.Violations.Should().ContainSingle(v => v.Contains($"holds {headings} \"## {Id}\" headings", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("VSDK0005", "vsdk0005")]
    [InlineData("Deprecated in 2.9.0 (VSDK0005)", "deprecated-in-290-vsdk0005")]
    public void Slug_ShouldMatchGitHubsAnchor_WhenGivenAHeading(string heading, string slug)
    {
        DeadModuleDeprecationScanner.Slug(heading).Should().Be(slug);
    }

    [Fact]
    public void UrlFormat_ShouldEndInTheSlugOfTheGuideSection_WhenRead()
    {
        Url.Should().EndWith("#" + DeadModuleDeprecationScanner.Slug(Id)).And.NotContain("{0}", "one anchor serves the whole id");
    }

    private static string Mark(string errorArgument = "") =>
        "[System.Obsolete(\"gone upstream\"" + errorArgument + ", DiagnosticId = \"" + Id + "\", UrlFormat = \"" + Url + "\")]";

    private static string Site(string open, string close) =>
        "namespace Pkg;\n\npublic static class Site\n{\n" + open + "\n    public static object Make() => new object();\n" + close + "\n}\n";

    private static FixtureTree GreenTree()
    {
        var tree = new FixtureTree();
        tree.Write("src/Pkg/Gone.cs", "namespace Pkg;\n\n" + Mark() + "\npublic sealed class GoneAction { }\n");
        tree.Write("src/Pkg/Pkg.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        tree.Write(DeadModuleDeprecationScanner.GuidePath, "# Guide\n\n## " + Id + "\n\nDeprecated in 2.9.0, removed in 2.10.0.\n");
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
