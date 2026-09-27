using System.Xml;
using System.Xml.Linq;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Detector for ADR-0004 D1: a package version is declared once, in <c>Directory.Packages.props</c>,
/// and no project file carries its own. Under central package management a <c>Version=</c> on a
/// <c>PackageReference</c> already fails restore (<c>NU1008</c>), but <c>VersionOverride</c> is
/// accepted wherever <c>CentralPackageVersionOverrideEnabled</c> is not <c>false</c>, and it is what
/// sets the floor a published package declares for that dependency. Reports each
/// <c>VersionOverride</c> as a <see cref="DecisionGuardSite"/> so the guard can match it against
/// <c>decision-guards-baseline.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule is a ratchet rather than <c>CentralPackageVersionOverrideEnabled=false</c>: disabling
/// overrides would force the one listed exception's floor to the central pin, and moving a published
/// dependency floor is a release decision of its own, not a side effect of a guard landing.
/// </para>
/// <para>
/// Both spellings of item metadata are read: the attribute (<c>&lt;PackageReference Include="X"
/// VersionOverride="1.0" /&gt;</c>) and the child element (<c>&lt;VersionOverride&gt;1.0&lt;/VersionOverride&gt;</c>),
/// which includes a default set for every reference through an <c>ItemDefinitionGroup</c>. MSBuild
/// reads metadata names without regard to case, so the match ignores case too. The file's XML
/// namespace is ignored, so a project in the legacy MSBuild namespace is read like an SDK-style one.
/// </para>
/// <para>
/// The key is canonical, not the raw text: <c>{item} {Include|Update|Remove}="{id}" VersionOverride="{value}"</c>,
/// built from the parsed XML, so attribute order, quoting, line wrapping and the choice between the
/// two spellings do not change it. The value is kept exactly as written — a <c>$(Property)</c> is not
/// evaluated — and it is part of the key, so moving a listed floor makes its entry stale and the new
/// value unlisted: the move has to be written into the baseline, where a reviewer sees it.
/// </para>
/// <para>
/// Detection reads XML elements and attributes, so a mention in an XML comment can never produce a
/// site.
/// </para>
/// </remarks>
internal static class VersionOverrideScanner
{
    /// <summary>The name this guard's entries carry in <c>decision-guards-baseline.json</c>.</summary>
    public const string GuardName = "package-version-override";

    private const string MetadataName = "VersionOverride";

    /// <summary>
    /// The MSBuild files a <c>VersionOverride</c> can hide in: every project, and every props or
    /// targets file a project imports — an override there applies to each importing project.
    /// </summary>
    private static readonly string[] ProjectFileExtensions = [".csproj", ".props", ".targets"];

    private static readonly string[] IdentityAttributes = ["Include", "Update", "Remove"];

    /// <summary>
    /// Every <c>VersionOverride</c> in the MSBuild file <paramref name="xml"/>, keyed canonically (see
    /// the remarks). Throws <see cref="InvalidDataException"/> naming <paramref name="path"/> when the
    /// file is not well-formed XML, rather than reporting nothing for it.
    /// </summary>
    public static IReadOnlyList<DecisionGuardSite> Scan(string xml, string path)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(path);

        var sites = new List<DecisionGuardSite>();
        foreach (var element in Parse(xml, path).Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (IsOverride(attribute.Name))
                    sites.Add(new DecisionGuardSite(path, LineOf(element), Key(element, attribute.Value)));
            }

            if (IsOverride(element.Name) && element.Parent is { } item)
                sites.Add(new DecisionGuardSite(path, LineOf(element), Key(item, element.Value.Trim())));
        }

        return sites;
    }

    /// <summary>
    /// Counts the <c>PackageReference</c> items in <paramref name="xml"/>, with or without an override.
    /// The guard's liveness floor sums it over the tree: once the one listed exception is gone the
    /// baseline is empty, and a reader that stopped seeing package references would then pass without
    /// inspecting one.
    /// </summary>
    public static int CountPackageReferences(string xml, string path)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(path);

        return Parse(xml, path).Descendants()
            .Count(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every <c>.csproj</c>, <c>.props</c> and <c>.targets</c> file of the checkout rooted at
    /// <paramref name="repoRoot"/>, as absolute paths in ordinal order. Build output (<c>bin</c>,
    /// <c>obj</c>) is skipped, and so is any directory below the root that holds a <c>.git</c> entry:
    /// that is another checkout — a nested worktree or repository, which a developer's clone can hold
    /// and which carries its own copy of every project — not a file this repository tracks.
    /// </summary>
    /// <remarks>
    /// The walk reads the file system rather than asking git, as every other guard in this project
    /// does, so it needs no <c>git</c> binary and no history. What it reads is a superset of the
    /// tracked project files — no tracked project lives under <c>bin</c>, <c>obj</c> or another
    /// checkout — so it cannot miss one. The cost is the other direction: in a developer's clone it
    /// also reads git-ignored project files (a local probe under <c>openspec/</c>, say), and an
    /// override in one of those fails the guard there, loudly and never in CI.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateProjectFiles(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"The repository root '{repoRoot}' does not exist.");

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(repoRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (ProjectFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    files.Add(file);
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is "bin" or "obj" or ".git")
                    continue;
                if (IsAnotherCheckout(child))
                    continue;
                if (new DirectoryInfo(child).LinkTarget is not null)
                    continue;

                pending.Push(child);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsAnotherCheckout(string directory)
    {
        var gitEntry = Path.Join(directory, ".git");
        return File.Exists(gitEntry) || Directory.Exists(gitEntry);
    }

    private static XDocument Parse(string xml, string path)
    {
        try
        {
            return XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{path} is not well-formed XML: {ex.Message}", ex);
        }
    }

    private static bool IsOverride(XName name) =>
        string.Equals(name.LocalName, MetadataName, StringComparison.OrdinalIgnoreCase);

    private static int LineOf(XElement element) => ((IXmlLineInfo)element).LineNumber;

    /// <summary>
    /// The canonical key of an override on <paramref name="item"/>: the item's name, its first identity
    /// attribute in <see cref="IdentityAttributes"/> order, and the value. Attribute names are written in
    /// their canonical casing whatever the file used, so a re-cased attribute is the same site.
    /// </summary>
    private static string Key(XElement item, string value)
    {
        foreach (var name in IdentityAttributes)
        {
            var identity = item.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (identity is not null)
                return $"{item.Name.LocalName} {name}=\"{identity.Value}\" {MetadataName}=\"{value}\"";
        }

        return $"{item.Name.LocalName} {MetadataName}=\"{value}\"";
    }
}
