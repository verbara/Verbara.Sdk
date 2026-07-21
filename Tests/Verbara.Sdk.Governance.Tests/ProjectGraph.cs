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

        var refs = new List<string>();
        var doc = XDocument.Parse(csprojXml);
        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName != "ProjectReference")
                continue;

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
                continue;

            if (IsAnalyzerReference(element))
                continue;

            refs.Add(BaseName(include));
        }

        return refs;
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
