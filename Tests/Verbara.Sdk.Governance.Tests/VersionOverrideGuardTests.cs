using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for ADR-0004 D1 (openspec change <c>a-decision-is-held-by-a-test-that-can-fail</c>,
/// spec requirement "A package version is declared once, centrally"; design D4): no project file
/// carries a <c>VersionOverride</c> that <c>decision-guards-baseline.json</c> does not list. One override
/// was in the tree when the guard landed — <c>Verbara.Sdk.Data.Npgsql</c>'s, whose entry records how it
/// got there — and the tree must match the list exactly: a new override fails, and so does an entry
/// whose override was removed or whose floor moved without the entry moving with it. Carries two
/// liveness floors — the project files walked and the package references read — and fixtures for
/// every spelling the scanner claims to see and every shape it must pass.
/// </summary>
public sealed class VersionOverrideGuardTests
{
    // Conservative floors, well below the tree as this guard landed (93 .csproj files plus
    // Directory.Build.props and Directory.Packages.props; ~430 PackageReference items). The second
    // floor keeps the guard honest once the one listed override is gone and the baseline is empty: a
    // reader that stopped seeing package references would then pass having inspected nothing.
    private const int MinimumScannedProjectFiles = 60;
    private const int MinimumPackageReferences = 200;

    [Fact]
    public void Guard_ShouldMatchTheBaselineExactly_WhenScanningEveryProjectFile()
    {
        var repoRoot = RepoRoot();

        var sites = new List<DecisionGuardSite>();
        foreach (var file in VersionOverrideScanner.EnumerateProjectFiles(repoRoot))
            sites.AddRange(VersionOverrideScanner.Scan(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var match = DecisionGuardBaseline.LoadCommitted().Match(VersionOverrideScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(BuildFailureMessage(match));
    }

    [Fact]
    public void Guard_ShouldScanManyProjectFiles_WhenWalkingTheRepository()
    {
        var repoRoot = RepoRoot();

        var files = VersionOverrideScanner.EnumerateProjectFiles(repoRoot)
            .Select(file => ToRelative(repoRoot, file))
            .ToList();

        files.Count.Should().BeGreaterThan(
            MinimumScannedProjectFiles,
            "the guard must walk every project under src/, Tests/, Examples/ and tools/; a near-zero " +
            "count means the locator broke and the override scan would be a false green");
        files.Should().Contain("Directory.Build.props").And.Contain(
            "Directory.Packages.props",
            "an override in an imported props file applies to every project that imports it, so the " +
            "walk must read the props files as well as the projects");
    }

    [Fact]
    public void Guard_ShouldReadManyPackageReferences_WhenWalkingTheRepository()
    {
        var repoRoot = RepoRoot();

        var references = VersionOverrideScanner.EnumerateProjectFiles(repoRoot)
            .Sum(file => VersionOverrideScanner.CountPackageReferences(File.ReadAllText(file), ToRelative(repoRoot, file)));

        references.Should().BeGreaterThanOrEqualTo(
            MinimumPackageReferences,
            "the reader must see the package references the projects actually declare; finding almost " +
            "none means the XML walk broke, and the guard would pass without inspecting a reference");
    }

    [Fact]
    public void GuardName_ShouldBeRegistered_WhenTheBaselineIsRead()
    {
        DecisionGuardBaseline.RegisteredGuards.Should().Contain(
            VersionOverrideScanner.GuardName,
            "an unregistered guard's baseline entries are reported as orphans, and a registration that " +
            "drifts from the scanner's name would make every entry of this guard one");
    }

    [Fact]
    public void Match_ShouldNameTheOverride_WhenAFixtureProjectCarriesOneTheBaselineDoesNotList()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Npgsql" />
                <PackageReference Include="Polly.Core" VersionOverride="8.5.0" />
              </ItemGroup>
            </Project>
            """;
        var sites = VersionOverrideScanner.Scan(csproj, "src/Fixture/Fixture.csproj");

        var match = DecisionGuardBaseline.Parse("""{ "entries": [] }""").Match(VersionOverrideScanner.GuardName, sites);

        match.IsExact.Should().BeFalse();
        match.Describe().Should().Contain("src/Fixture/Fixture.csproj:4")
            .And.Contain("PackageReference Include=\"Polly.Core\" VersionOverride=\"8.5.0\"");
    }

    [Fact]
    public void Match_ShouldBeExact_WhenTheBaselineListsTheOverrideAsItIsQuoted()
    {
        // The key is pasted from the failure message into the entry, so the scanner's key and the
        // matcher's normalization must agree on it.
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" VersionOverride="10.0.9" />
              </ItemGroup>
            </Project>
            """;
        const string baseline = """
            { "entries": [
              { "guard": "package-version-override", "path": "src/Fixture/Fixture.csproj",
                "key": "PackageReference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\" VersionOverride=\"10.0.9\"",
                "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = VersionOverrideScanner.Scan(csproj, "src/Fixture/Fixture.csproj");

        var match = DecisionGuardBaseline.Parse(baseline).Match(VersionOverrideScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void Match_ShouldReportTheNewFloorAndTheStaleEntry_WhenAListedOverrideChangesItsVersion()
    {
        // The version is part of the key, so a floor move cannot ride on the old entry: the move has
        // to be written into the baseline, where a reviewer sees it.
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" VersionOverride="10.0.12" />
              </ItemGroup>
            </Project>
            """;
        const string baseline = """
            { "entries": [
              { "guard": "package-version-override", "path": "src/Fixture/Fixture.csproj",
                "key": "PackageReference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\" VersionOverride=\"10.0.9\"",
                "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = VersionOverrideScanner.Scan(csproj, "src/Fixture/Fixture.csproj");

        var match = DecisionGuardBaseline.Parse(baseline).Match(VersionOverrideScanner.GuardName, sites);

        match.Unlisted.Should().ContainSingle().Which.Key.Should().EndWith("VersionOverride=\"10.0.12\"");
        match.Stale.Should().ContainSingle().Which.Key.Should().EndWith("VersionOverride=\"10.0.9\"");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOverrideIsAnAttribute()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Npgsql" VersionOverride="10.0.2" />
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Should().ContainSingle().Which.Should().Be(
            new DecisionGuardSite("x.csproj", 3, "PackageReference Include=\"Npgsql\" VersionOverride=\"10.0.2\""));
    }

    [Fact]
    public void Scan_ShouldFlagWithTheSameKey_WhenTheOverrideIsAChildElement()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Npgsql">
                  <VersionOverride>
                    10.0.2
                  </VersionOverride>
                </PackageReference>
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Should().ContainSingle().Which.Should().Be(
            new DecisionGuardSite("x.csproj", 4, "PackageReference Include=\"Npgsql\" VersionOverride=\"10.0.2\""));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheMetadataNameIsNotPascalCased()
    {
        // MSBuild reads metadata names without regard to case, so NuGet sees these as VersionOverride.
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference include="Npgsql" versionoverride="10.0.2" />
                <PackageReference Include="Polly.Core"><VERSIONOVERRIDE>8.5.0</VERSIONOVERRIDE></PackageReference>
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Select(s => s.Key).Should().Equal(
            "PackageReference Include=\"Npgsql\" VersionOverride=\"10.0.2\"",
            "PackageReference Include=\"Polly.Core\" VersionOverride=\"8.5.0\"");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheProjectIsInTheLegacyMsBuildNamespace()
    {
        const string csproj = """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PackageReference Include="Npgsql" VersionOverride="10.0.2" />
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Should().ContainSingle().Which.Key.Should().Be("PackageReference Include=\"Npgsql\" VersionOverride=\"10.0.2\"");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAnUpdateItemOrAnItemDefinitionSetsTheOverride()
    {
        const string props = """
            <Project>
              <ItemGroup>
                <PackageReference Update="Npgsql" VersionOverride="10.0.2" />
              </ItemGroup>
              <ItemDefinitionGroup>
                <PackageReference>
                  <VersionOverride>1.0.0</VersionOverride>
                </PackageReference>
              </ItemDefinitionGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(props, "Directory.Build.props");

        sites.Select(s => s.Key).Should().Equal(
            "PackageReference Update=\"Npgsql\" VersionOverride=\"10.0.2\"",
            "PackageReference VersionOverride=\"1.0.0\"");
    }

    [Fact]
    public void Scan_ShouldKeepTheValueAsWritten_WhenTheOverrideIsAPropertyReference()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Npgsql" VersionOverride="$(NpgsqlFloor)" />
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Should().ContainSingle().Which.Key.Should().Be("PackageReference Include=\"Npgsql\" VersionOverride=\"$(NpgsqlFloor)\"");
    }

    [Fact]
    public void Scan_ShouldPass_WhenEveryReferenceTakesTheCentralVersion()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- A VersionOverride="1.0" in a comment is not an override. -->
              <PropertyGroup>
                <Description>Notes on VersionOverride and how the floor is set.</Description>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Npgsql" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" PrivateAssets="all" />
                <ProjectReference Include="..\Verbara.Sdk\Verbara.Sdk.csproj" />
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(csproj, "x.csproj");

        sites.Should().BeEmpty();
        VersionOverrideScanner.CountPackageReferences(csproj, "x.csproj").Should().Be(2, "the reader saw both references");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheCentralFileDeclaresTheVersions()
    {
        const string props = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Npgsql" Version="10.0.3" />
                <GlobalPackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" />
              </ItemGroup>
            </Project>
            """;

        var sites = VersionOverrideScanner.Scan(props, "Directory.Packages.props");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldNameTheFile_WhenTheXmlIsMalformed()
    {
        var act = () => VersionOverrideScanner.Scan("<Project><ItemGroup></Project>", "src/Broken/Broken.csproj");

        act.Should().Throw<InvalidDataException>().WithMessage("*src/Broken/Broken.csproj*");
    }

    [Fact]
    public void EnumerateProjectFiles_ShouldSkipBuildOutputAndNestedCheckouts_WhenWalkingATree()
    {
        // A developer's clone can hold other checkouts of this repository (git worktrees under
        // .worktrees/, a nested clone), each with its own copy of every project. They are not files
        // this repository tracks, and reading them would report the listed override once per copy.
        var root = Path.Join(Path.GetTempPath(), "version-override-walk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Touch(root, ".git/HEAD");
            Touch(root, "Directory.Build.props");
            Touch(root, "Root.csproj");
            Touch(root, "lib/P/P.csproj");
            Touch(root, "lib/P/build/P.targets");
            Touch(root, "lib/P/README.md");
            Touch(root, "lib/P/obj/P.csproj.nuget.g.props");
            Touch(root, "lib/P/bin/Release/P.targets");
            Touch(root, ".worktrees/feature/.git");
            Touch(root, ".worktrees/feature/lib/P/P.csproj");
            Touch(root, "vendor/clone/.git/HEAD");
            Touch(root, "vendor/clone/Q.csproj");

            var files = VersionOverrideScanner.EnumerateProjectFiles(root)
                .Select(file => ToRelative(root, file))
                .ToList();

            files.Should().Equal("Directory.Build.props", "Root.csproj", "lib/P/P.csproj", "lib/P/build/P.targets");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Touch(string root, string relative)
    {
        var path = Path.Join(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }

    private static string RepoRoot() => Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(DecisionGuardMatch match)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "ADR-0004 D1: a package version is declared once, in Directory.Packages.props. A " +
            "VersionOverride sets the floor a published package declares for that dependency, apart " +
            "from the central pin, and nothing moves it when the pin moves: the override in the tree when " +
            "this guard landed had stayed at 10.0.9 while its pin went to 10.0.12.");
        sb.AppendLine(
            "Remove the override and take the central version. The baseline lists only the override that " +
            "was in the tree when this guard landed, and it only shrinks. The version is part of the key, " +
            "so moving that listed floor means rewriting its entry: a release decision, made where a " +
            "reviewer sees it.");
        sb.Append(match.Describe());
        return sb.ToString();
    }
}
