using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process architecture guard (ADR-0014 §2 G4): fails the build if an infrastructure-provider
/// package (base name ending in a <see cref="ProjectGraph.ProviderSuffixes"/> — e.g. <c>.Postgres</c>,
/// <c>.Redis</c>, <c>.Nats</c>) is compile-time <c>ProjectReference</c>d by a NON-provider package.
/// Providers are concrete backing stores / transports that plug in via DI at the app composition
/// root; a non-provider taking a compile-time dependency on one would hard-wire an infrastructure
/// choice and break the open-core seam. (Verified true in Sdk today.) Includes a liveness self-test
/// (the graph must actually cover many projects) and parse self-tests for the analyzer-exclusion.
/// </summary>
public sealed class LayeringGuardTests
{
    // Conservative floor: a floor well below the real src-project count (~29) defeats the "graph
    // is empty -> false green" failure mode while tolerating churn.
    private const int MinimumScannedProjects = 20;

    [Fact]
    public void Layering_ShouldScanManyProjects_WhenBuildingGraph()
    {
        var graph = ProjectGraph.Build();

        graph.Count.Should().BeGreaterThan(
            MinimumScannedProjects,
            "the guard must walk the real src project graph; a near-empty graph means the locator " +
            "broke and the layering check would be a false green");
    }

    [Fact]
    public void Layering_NonProviderMustNotReferenceProvider()
    {
        var graph = ProjectGraph.Build();

        var violations = new List<string>();
        foreach (var (package, references) in graph)
        {
            if (IsProvider(package))
                continue;

            foreach (var reference in references)
            {
                if (IsProvider(reference))
                    violations.Add($"{package} -> {reference}");
            }
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void ExtractProjectReferences_ShouldExcludeAnalyzerReference()
    {
        const string csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <ItemGroup>\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk\\Verbara.Sdk.csproj\" />\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk.Gen\\Verbara.Sdk.Gen.csproj\"\n" +
            "                      OutputItemType=\"Analyzer\"\n" +
            "                      ReferenceOutputAssembly=\"false\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>";

        var refs = ProjectGraph.ExtractProjectReferences(csproj);

        refs.Should().ContainSingle().Which.Should().Be("Verbara.Sdk");
    }

    [Fact]
    public void ExtractProjectReferences_ShouldExcludeReferenceOutputAssemblyFalse()
    {
        const string csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <ItemGroup>\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk.Gen\\Verbara.Sdk.Gen.csproj\" ReferenceOutputAssembly=\"false\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>";

        var refs = ProjectGraph.ExtractProjectReferences(csproj);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void ExtractProjectReferences_ShouldReturnBaseName_WhenPlainReference()
    {
        const string csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <ItemGroup>\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk.Sessions\\Verbara.Sdk.Sessions.csproj\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>";

        var refs = ProjectGraph.ExtractProjectReferences(csproj);

        refs.Should().ContainSingle().Which.Should().Be("Verbara.Sdk.Sessions");
    }

    private static bool IsProvider(string package)
    {
        foreach (var suffix in ProjectGraph.ProviderSuffixes)
        {
            if (package.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string BuildFailureMessage(List<string> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" non-provider package(s) take a compile-time ProjectReference on an infrastructure provider:");
        foreach (var v in violations.OrderBy(v => v, StringComparer.Ordinal))
            sb.Append("  ").AppendLine(v);

        sb.AppendLine(
            "Provider packages (name ends in .Postgres | .Redis | .Nats) are concrete backing " +
            "stores / transports that must be wired via DI at the app composition root, never " +
            "referenced at compile time by a non-provider package (that would hard-wire the " +
            "infrastructure choice and break the open-core seam).");
        return sb.ToString();
    }
}
