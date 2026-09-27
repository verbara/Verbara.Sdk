using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process architecture guard (verbara-meta/ADR-0014 §2 G4): fails the build if an infrastructure-provider
/// package (base name ending in a <see cref="ProjectGraph.ProviderSuffixes"/> — e.g. <c>.Postgres</c>,
/// <c>.Redis</c>, <c>.Nats</c>) is compile-time <c>ProjectReference</c>d by a NON-provider package.
/// Providers are concrete backing stores / transports that plug in via DI at the app composition
/// root; a non-provider taking a compile-time dependency on one would hard-wire an infrastructure
/// choice and break the open-core seam. (Verified true in Sdk today.) Includes a liveness self-test
/// (the graph must actually cover many projects) and parse self-tests for the analyzer-exclusion.
/// </summary>
/// <remarks>
/// It also holds the half of ADR-0012 that the code keeps (spec requirement "No transport package
/// references the Live package"): <c>Verbara.Sdk.Live</c> is an aggregate root the transports feed,
/// so neither <c>Verbara.Sdk.Ami</c> nor <c>Verbara.Sdk.Ari</c> may reach it. Only one direction was
/// held before, and by accident: Live references AMI, so an AMI-to-Live reference is a circular
/// project reference the build refuses. Nothing refused an ARI-to-Live reference. The check follows
/// references transitively, because a transport that reaches Live through an intermediate package
/// pulls Live into every consumer of that transport just the same.
/// </remarks>
public sealed class LayeringGuardTests
{
    // Conservative floor: a floor well below the real src-project count (~29) defeats the "graph
    // is empty -> false green" failure mode while tolerating churn.
    private const int MinimumScannedProjects = 20;

    /// <summary>The transport packages ADR-0012 makes data sources of <see cref="LivePackage"/>.</summary>
    private static readonly string[] TransportPackages = ["Verbara.Sdk.Ami", "Verbara.Sdk.Ari"];

    /// <summary>The aggregate root the transports feed and must never reference.</summary>
    private const string LivePackage = "Verbara.Sdk.Live";

    [Fact]
    public void Layering_TransportMustNotReferenceLive()
    {
        var graph = ProjectGraph.Build();

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().BeEmpty(BuildTransportFailureMessage(chains));
    }

    [Fact]
    public void Layering_ShouldFindTheTransportsAndLive_WhenBuildingGraph()
    {
        // A rename of any of the three would make the transport check pass having inspected nothing,
        // and Live's own reference to AMI proves the graph's edges are read, not just its nodes.
        var graph = ProjectGraph.Build();

        graph.Keys.Should().Contain(
            [.. TransportPackages, LivePackage],
            "the transport-to-Live check names these packages; one that is missing from the graph " +
            "can never be reported");
        graph[LivePackage].Should().Contain(
            "Verbara.Sdk.Ami",
            "Live consumes AMI events (ADR-0012), so the graph must carry that edge; without it the " +
            "references are not being read and the check would be a false green");
    }

    [Fact]
    public void FindReferenceChains_ShouldReportTheEdge_WhenAFixtureTransportReferencesLive()
    {
        var graph = FixtureGraph(("Verbara.Sdk.Ari", ["Verbara.Sdk", "Verbara.Sdk.Resilience", LivePackage]));

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().Equal("Verbara.Sdk.Ari -> Verbara.Sdk.Live");
    }

    [Fact]
    public void FindReferenceChains_ShouldReportTheEdge_WhenTheFixtureIsReadFromCsprojXml()
    {
        // The whole path the guard takes on the real tree: csproj text, reference extraction, graph.
        const string ariCsproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <ItemGroup>\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk\\Verbara.Sdk.csproj\" />\n" +
            "    <ProjectReference Include=\"..\\Verbara.Sdk.Live\\Verbara.Sdk.Live.csproj\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>";
        var graph = FixtureGraph(("Verbara.Sdk.Ari", ProjectGraph.ExtractProjectReferences(ariCsproj)));

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().Equal("Verbara.Sdk.Ari -> Verbara.Sdk.Live");
    }

    [Fact]
    public void FindReferenceChains_ShouldReportTheShortestChain_WhenATransportReachesLiveThroughAnotherPackage()
    {
        var graph = FixtureGraph(
            ("Verbara.Sdk.Ami", ["Verbara.Sdk", "Verbara.Sdk.Sessions"]),
            ("Verbara.Sdk.Sessions", ["Verbara.Sdk.Sessions.Core"]),
            ("Verbara.Sdk.Sessions.Core", [LivePackage]),
            ("Verbara.Sdk.Ari", ["Verbara.Sdk.Hub"]),
            ("Verbara.Sdk.Hub", ["Verbara.Sdk.Sessions", LivePackage]));

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().Equal(
            "Verbara.Sdk.Ami -> Verbara.Sdk.Sessions -> Verbara.Sdk.Sessions.Core -> Verbara.Sdk.Live",
            "Verbara.Sdk.Ari -> Verbara.Sdk.Hub -> Verbara.Sdk.Live");
    }

    [Fact]
    public void FindReferenceChains_ShouldReportNothing_WhenOnlyLiveReferencesATransport()
    {
        // The direction ADR-0012 decides: Live consumes the transports.
        var graph = FixtureGraph(
            (LivePackage, ["Verbara.Sdk", "Verbara.Sdk.Ami"]),
            ("Verbara.Sdk.Ami", ["Verbara.Sdk", "Verbara.Sdk.Resilience"]),
            ("Verbara.Sdk.Ari", ["Verbara.Sdk", "Verbara.Sdk.Resilience"]),
            ("Verbara.Sdk.Sessions", [LivePackage]));

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().BeEmpty();
    }

    [Fact]
    public void FindReferenceChains_ShouldTerminate_WhenTheFixtureGraphHasACycle()
    {
        var graph = FixtureGraph(
            ("Verbara.Sdk.Ari", ["Verbara.Sdk.A"]),
            ("Verbara.Sdk.A", ["Verbara.Sdk.B"]),
            ("Verbara.Sdk.B", ["Verbara.Sdk.A", "Verbara.Sdk.Ari"]));

        var chains = ProjectGraph.FindReferenceChains(graph, TransportPackages, LivePackage);

        chains.Should().BeEmpty();
    }

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

            violations.AddRange(references.Where(IsProvider).Select(reference => $"{package} -> {reference}"));
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

    private static Dictionary<string, IReadOnlyList<string>> FixtureGraph(
        params (string Package, IReadOnlyList<string> References)[] nodes) =>
        nodes.ToDictionary(node => node.Package, node => node.References, StringComparer.Ordinal);

    private static string BuildTransportFailureMessage(IReadOnlyList<string> chains)
    {
        var sb = new StringBuilder();
        sb.Append(chains.Count)
            .AppendLine(" transport package(s) reference Verbara.Sdk.Live (ADR-0012):");
        foreach (var chain in chains)
            sb.Append("  ").AppendLine(chain);

        sb.AppendLine(
            "Live is the aggregate root the transports feed: it consumes AMI (and, by ADR-0012's " +
            "intent, ARI) events. A transport that references Live, directly or through another " +
            "package, turns a data source into an owner and forces Live into every consumer of that " +
            "transport. Move the shared type into Verbara.Sdk or the transport package instead.");
        return sb.ToString();
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
