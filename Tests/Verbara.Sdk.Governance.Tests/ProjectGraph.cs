using System.Xml.Linq;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Builds the compile-time project-reference graph of the <c>src/</c> tree by reading each
/// <c>*.csproj</c> as XML and extracting its real <c>&lt;ProjectReference&gt;</c> targets (as the
/// referenced project's base name). Analyzer / source-generator references — those carrying
/// <c>OutputItemType="Analyzer"</c> or <c>ReferenceOutputAssembly="false"</c> — are excluded: they
/// are a build-time tooling edge, not a runtime layering dependency. Feeds
/// <see cref="LayeringGuardTests"/>.
/// </summary>
internal static class ProjectGraph
{
    /// <summary>
    /// Infrastructure-provider package suffixes. A package whose base name ends with one of these
    /// is a provider (a concrete backing store / transport) that plugs in via DI at the app
    /// composition root; it must never be a compile-time dependency of a non-provider package.
    /// </summary>
    public static readonly string[] ProviderSuffixes = [".Postgres", ".Redis", ".Nats"];

    /// <summary>
    /// Maps each <c>src/</c> package (its base name) to the base names of its non-analyzer
    /// <c>&lt;ProjectReference&gt;</c> targets.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var projectPath in SrcTreeSource.EnumerateSrcProjects())
        {
            var package = BaseName(projectPath);
            var xml = File.ReadAllText(projectPath);
            graph[package] = ExtractProjectReferences(xml);
        }

        return graph;
    }

    /// <summary>
    /// Parses a csproj's XML text and returns the base names of every <c>&lt;ProjectReference&gt;</c>
    /// whose element does NOT carry <c>OutputItemType="Analyzer"</c> or
    /// <c>ReferenceOutputAssembly="false"</c>. Namespace-agnostic (SDK-style csproj is unqualified,
    /// but match on local name defensively). Exposed for the parse self-tests.
    /// </summary>
    public static IReadOnlyList<string> ExtractProjectReferences(string csprojXml)
    {
        ArgumentNullException.ThrowIfNull(csprojXml);

        // OfType<string>() drops a missing Include and the Length check an empty one. Both
        // predicates are pure, so testing IsAnalyzerReference first selects the same elements.
        return XDocument.Parse(csprojXml)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference" && !IsAnalyzerReference(element))
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(include => include.Length > 0)
            .Select(BaseName)
            .ToList();
    }

    /// <summary>
    /// For each package in <paramref name="sources"/> that can reach <paramref name="target"/> through
    /// the graph's references, directly or through intermediate packages, returns the shortest such
    /// chain as <c>"A -&gt; B -&gt; target"</c>. Sources are reported in ordinal order; a source absent
    /// from the graph has no outgoing references and reaches nothing.
    /// </summary>
    public static IReadOnlyList<string> FindReferenceChains(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        IEnumerable<string> sources,
        string target)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);

        var chains = new List<string>();
        foreach (var source in sources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var chain = ShortestChain(graph, source, target);
            if (chain is not null)
                chains.Add(string.Join(" -> ", chain));
        }

        return chains;
    }

    /// <summary>
    /// Breadth-first search from <paramref name="source"/>, visiting references in ordinal order so
    /// the chain reported is the same on every machine. Each package is visited once, so a cycle ends
    /// the walk instead of looping.
    /// </summary>
    private static List<string>? ShortestChain(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        string source,
        string target)
    {
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { source };
        var queue = new Queue<string>();
        queue.Enqueue(source);

        while (queue.Count > 0)
        {
            var package = queue.Dequeue();
            if (!graph.TryGetValue(package, out var references))
                continue;

            foreach (var reference in references.Order(StringComparer.Ordinal))
            {
                if (!visited.Add(reference))
                    continue;

                cameFrom[reference] = package;
                if (string.Equals(reference, target, StringComparison.Ordinal))
                    return Unwind(cameFrom, source, target);

                queue.Enqueue(reference);
            }
        }

        return null;
    }

    private static List<string> Unwind(Dictionary<string, string> cameFrom, string source, string target)
    {
        var chain = new List<string> { target };
        for (var package = target; !string.Equals(package, source, StringComparison.Ordinal);)
        {
            package = cameFrom[package];
            chain.Add(package);
        }

        chain.Reverse();
        return chain;
    }

    private static bool IsAnalyzerReference(XElement element)
    {
        var outputItemType = element.Attribute("OutputItemType")?.Value;
        if (string.Equals(outputItemType, "Analyzer", StringComparison.OrdinalIgnoreCase))
            return true;

        var referenceOutputAssembly = element.Attribute("ReferenceOutputAssembly")?.Value;
        return string.Equals(referenceOutputAssembly, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the directory and the <c>.csproj</c> extension from an <c>Include</c> path (which uses
    /// Windows-style <c>\</c> separators in csproj) to yield the referenced project's base name.
    /// </summary>
    private static string BaseName(string projectReference)
    {
        var normalized = projectReference.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".csproj".Length]
            : fileName;
    }
}
