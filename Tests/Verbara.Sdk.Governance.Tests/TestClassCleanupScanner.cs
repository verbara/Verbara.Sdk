using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>One source file handed to <see cref="TestClassCleanupScanner"/>.</summary>
internal sealed record TestClassCleanupSource(string Path, string Text);

/// <summary>
/// A runner-managed type — a test class or a class/collection fixture — whose interface closure
/// contains <c>IAsyncDisposable</c>. <see cref="TypeName"/> is namespace-qualified, containing types
/// included; <see cref="Path"/>:<see cref="Line"/> is the declaration that brings the interface in
/// (or the type's first declaration when it arrives through a base type).
/// </summary>
internal sealed record TestClassCleanupViolation(string Path, int Line, string TypeName, string Role);

/// <summary>What one scan found: the violations, and how many runner-managed types it judged.</summary>
internal sealed record TestClassCleanupResult(
    IReadOnlyList<TestClassCleanupViolation> Violations,
    int TestClassCount,
    int FixtureCount);

/// <summary>
/// Pure, I/O-free detector for "a test class's cleanup is one the runner calls" (ADR-0066): under the
/// pinned xunit 2.x runner, a runner-managed type must not have <c>IAsyncDisposable</c> anywhere in its
/// interface closure, whatever else it implements. xunit 2.9.3 disposes a test class and its class and
/// collection fixtures through <c>IAsyncLifetime</c> and <c>IDisposable</c> only; its runner assembly
/// has no reference to <c>IAsyncDisposable</c>, so an <c>IAsyncDisposable.DisposeAsync</c> on such a
/// type compiles, reads as correct, and is never called.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: the pinned 2.x runner.</b> In xunit v3, <c>IAsyncLifetime</c> derives from
/// <c>IAsyncDisposable</c> and the runner calls it, so this rule is then wrong, not merely unnecessary.
/// This scanner sees declared names only, not v3's inherited interface; it is rewritten or retired in
/// the v3 migration.
/// </para>
/// <para>
/// Syntax only, no semantic model. Every type declaration is keyed by its namespace-qualified name,
/// containing types and generic arity included, and <b>partial declarations are merged</b> under that
/// key before anything is judged. A name in a base list resolves against those keys through the
/// declaring type's containing types, its namespace and each enclosing namespace, then its
/// <c>using</c> directives — never by simple name alone, because two test classes in different
/// namespaces share a name.
/// </para>
/// <para>
/// <b>A test class</b> is a non-abstract class whose own direct members include a method carrying an
/// attribute whose name ends in <c>Fact</c> or <c>Theory</c> (so the repo's custom
/// <c>RequiresDockerFact</c> and the like count), or that derives from a class that does. A nested
/// class is judged on its own members. <b>A fixture</b> is a type named as the argument of an
/// <c>IClassFixture&lt;T&gt;</c> or <c>ICollectionFixture&lt;T&gt;</c> in any base list. The
/// interface names match in every spelling: simple, <c>System.</c> / <c>Xunit.</c> qualified,
/// <c>global::</c> and through a <c>using</c> alias.
/// </para>
/// </remarks>
internal static class TestClassCleanupScanner
{
    private const string AsyncDisposable = "IAsyncDisposable";
    private const string AsyncLifetime = "IAsyncLifetime";
    private const string Disposable = "IDisposable";

    /// <summary>The rule and its fix, for the guard's failure message.</summary>
    public const string RuleText =
        "ADR-0066: xunit 2.x never calls IAsyncDisposable.DisposeAsync on a test class or fixture, so a " +
        "cleanup written there is dead code. Implement IAsyncLifetime (InitializeAsync => Task.CompletedTask; " +
        "Task DisposeAsync), never both interfaces; delete GC.SuppressFinalize(this) (CA1816); and if CA1001 " +
        "fires add [SuppressMessage(\"Reliability\", \"CA1001:Types that own disposable fields should be " +
        "disposable\", Justification = \"Disposed via IAsyncLifetime\")].";

    /// <summary>
    /// The guard's failure message: the rule, the fix, and each violation as <c>path:line type (role)</c>.
    /// </summary>
    public static string BuildFailureMessage(IReadOnlyList<TestClassCleanupViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(RuleText);
        sb.Append(violations.Count).AppendLine(" runner-managed type(s) with IAsyncDisposable in their closure:");
        foreach (var v in violations)
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append(' ').Append(v.TypeName)
                .Append(" (").Append(v.Role).AppendLine(")");
        return sb.ToString();
    }

    /// <summary>Scans <paramref name="sources"/> as one tree and returns every violation, sorted.</summary>
    public static TestClassCleanupResult Scan(IEnumerable<TestClassCleanupSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var parsed = sources
            .Select(s => (s.Path, Root: (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(s.Text).GetRoot()))
            .ToList();

        var globalUsings = CollectGlobalUsings(parsed);
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        foreach (var (path, root) in parsed)
        {
            var project = ProjectKey(path);
            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var key = KeyOf(decl);
                if (!types.TryGetValue(key, out var info))
                {
                    info = new TypeInfo(key);
                    types.Add(key, info);
                }

                info.Parts.Add(new TypePart(decl, path, ContextOf(decl, globalUsings.GetValueOrDefault(project))));
            }
        }

        var fixtures = new HashSet<string>(
            types.Values
                .SelectMany(info => info.Parts)
                .SelectMany(part => FixtureArguments(part.Declaration).Select(argument => Resolve(argument, part.Context, types)))
                .OfType<string>(),
            StringComparer.Ordinal);

        var violations = new List<TestClassCleanupViolation>();
        var testClasses = 0;
        var fixtureCount = 0;
        foreach (var info in types.Values)
        {
            var isTest = IsTestClass(info, types);
            var isFixture = fixtures.Contains(info.Key);
            if (isTest)
                testClasses++;
            if (isFixture)
                fixtureCount++;
            if (!isTest && !isFixture)
                continue;

            if (!HasAsyncDisposable(info, types, new HashSet<string>(StringComparer.Ordinal), out var direct))
                continue;

            var anchor = direct ?? info.Parts.OrderBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line).First();
            violations.Add(new TestClassCleanupViolation(
                anchor.Path,
                anchor.Line,
                info.Key,
                isTest ? "test class" : "fixture"));
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return new TestClassCleanupResult(violations, testClasses, fixtureCount);
    }

    private static bool IsTestClass(TypeInfo info, Dictionary<string, TypeInfo> types)
    {
        if (!info.IsClass || info.IsAbstract)
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = info;
        while (current is not null && visited.Add(current.Key))
        {
            if (current.HasOwnTestMethod)
                return true;

            current = BaseClassOf(current, types);
        }

        return false;
    }

    private static TypeInfo? BaseClassOf(TypeInfo info, Dictionary<string, TypeInfo> types) =>
        info.Parts
            .SelectMany(part => BaseTypes(part.Declaration).Select(baseType => Resolve(baseType.Type, part.Context, types)))
            .OfType<string>()
            .Where(resolved => resolved != info.Key && types[resolved].IsClass)
            .Select(resolved => types[resolved])
            .FirstOrDefault();

    /// <summary>
    /// Walks the interface closure of <paramref name="info"/> through every base-list entry that
    /// resolves to a type declared in the scanned tree. True when <c>IAsyncDisposable</c> is
    /// in it; <paramref name="direct"/> is the part that names it when <paramref name="info"/> itself does.
    /// </summary>
    private static bool HasAsyncDisposable(
        TypeInfo info, Dictionary<string, TypeInfo> types, HashSet<string> visited, out TypePart? direct)
    {
        direct = null;
        if (!visited.Add(info.Key))
            return false;

        direct = info.Parts.FirstOrDefault(part =>
            BaseTypes(part.Declaration).Any(baseType => WellKnownName(baseType.Type, part.Context) == AsyncDisposable));
        if (direct is not null)
            return true;

        // Lazy, in declaration order, stopping at the first base that reaches it — `visited` is shared
        // with the recursion, so the order of the walk is the order of the loop it replaces.
        return info.Parts
            .SelectMany(part => BaseTypes(part.Declaration)
                .Where(baseType => WellKnownName(baseType.Type, part.Context) is null)
                .Select(baseType => Resolve(baseType.Type, part.Context, types)))
            .OfType<string>()
            .Where(resolved => resolved != info.Key)
            .Any(resolved => HasAsyncDisposable(types[resolved], types, visited, out _));
    }

    /// <summary>The entries of a declaration's base list, empty when it has none.</summary>
    private static SeparatedSyntaxList<BaseTypeSyntax> BaseTypes(TypeDeclarationSyntax declaration) =>
        declaration.BaseList?.Types ?? default;

    /// <summary>
    /// <c>IAsyncDisposable</c>, <c>IAsyncLifetime</c> or <c>IDisposable</c> when <paramref name="type"/>
    /// names one of them in any spelling (a <c>using</c> alias included); otherwise null.
    /// </summary>
    private static string? WellKnownName(TypeSyntax type, ResolutionContext context)
    {
        var name = Dotted(type);
        if (name is null)
            return null;

        var segments = name.Value.Text.Split('.');
        if (!name.Value.IsGlobal && context.Aliases.TryGetValue(segments[0], out var aliased))
            segments = [.. aliased.Split('.'), .. segments.Skip(1)];

        var last = segments[^1];
        return last is AsyncDisposable or AsyncLifetime or Disposable ? last : null;
    }

    private static IEnumerable<TypeSyntax> FixtureArguments(TypeDeclarationSyntax declaration) =>
        BaseTypes(declaration)
            .Select(baseType => baseType.Type switch
            {
                GenericNameSyntax g => g,
                QualifiedNameSyntax { Right: GenericNameSyntax g } => g,
                AliasQualifiedNameSyntax { Name: GenericNameSyntax g } => g,
                _ => null,
            })
            .OfType<GenericNameSyntax>()
            .Where(generic => generic.TypeArgumentList.Arguments.Count == 1
                && generic.Identifier.ValueText is "IClassFixture" or "ICollectionFixture")
            .Select(generic => generic.TypeArgumentList.Arguments[0]);

    /// <summary>Resolves a type name to the key of a type declared in the scanned tree, or null.</summary>
    private static string? Resolve(TypeSyntax type, ResolutionContext context, Dictionary<string, TypeInfo> types)
    {
        var name = Dotted(type);
        if (name is null)
            return null;

        var text = name.Value.Text;
        if (name.Value.IsGlobal)
            return types.ContainsKey(text) ? text : null;

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? text : text[..dot];
        if (context.Aliases.TryGetValue(head, out var aliased))
        {
            var full = dot < 0 ? aliased : aliased + text[dot..];
            return types.ContainsKey(full) ? full : null;
        }

        var nested = context.ContainingTypes.Select(container => container + "." + text).FirstOrDefault(types.ContainsKey);
        if (nested is not null)
            return nested;

        for (var ns = context.Namespace; ; ns = ParentNamespace(ns))
        {
            var candidate = ns.Length == 0 ? text : ns + "." + text;
            if (types.ContainsKey(candidate))
                return candidate;
            if (ns.Length == 0)
                break;
        }

        return context.Usings.Select(imported => imported + "." + text).FirstOrDefault(types.ContainsKey);
    }

    private static string ParentNamespace(string ns)
    {
        var dot = ns.LastIndexOf('.');
        return dot < 0 ? string.Empty : ns[..dot];
    }

    /// <summary>
    /// The dotted form of a name — each segment with its generic arity (<c>Base`1</c>) — and whether it
    /// was written <c>global::</c>. Null for a type that is not a name (an array, a tuple, a keyword).
    /// </summary>
    private static (string Text, bool IsGlobal)? Dotted(TypeSyntax type) => type switch
    {
        AliasQualifiedNameSyntax a => Dotted(a.Name) is { } inner ? (inner.Text, a.Alias.Identifier.ValueText == "global") : null,
        QualifiedNameSyntax q => Dotted(q.Left) is { } left && Dotted(q.Right) is { } right
            ? (left.Text + "." + right.Text, left.IsGlobal)
            : null,
        GenericNameSyntax g => (g.Identifier.ValueText + "`" + g.TypeArgumentList.Arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
        IdentifierNameSyntax i => (i.Identifier.ValueText, false),
        _ => null,
    };

    private static string Segment(TypeDeclarationSyntax decl) =>
        decl.TypeParameterList is { Parameters.Count: > 0 } list
            ? decl.Identifier.ValueText + "`" + list.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : decl.Identifier.ValueText;

    private static string KeyOf(TypeDeclarationSyntax decl)
    {
        var segments = new List<string> { Segment(decl) };
        foreach (var ancestor in decl.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax outer)
                segments.Add(Segment(outer));
            else if (ancestor is BaseNamespaceDeclarationSyntax ns)
                segments.Add(ns.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        segments.Reverse();
        return string.Join('.', segments);
    }

    private static string NamespaceOf(TypeDeclarationSyntax decl) =>
        string.Join('.', decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(ns => ns.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal)));

    private static ResolutionContext ContextOf(TypeDeclarationSyntax decl, UsingSet? global)
    {
        var containing = decl.Ancestors().OfType<TypeDeclarationSyntax>().Select(KeyOf).ToList();

        var usings = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        // Innermost scope first: a namespace's own usings, then the compilation unit's, then the
        // project's global usings.
        foreach (var ns in decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            AddUsings(ns.Usings, usings, aliases);
        if (decl.SyntaxTree.GetRoot() is CompilationUnitSyntax unit)
            AddUsings(unit.Usings, usings, aliases);
        if (global is not null)
        {
            usings.AddRange(global.Namespaces);
            foreach (var (alias, target) in global.Aliases)
                aliases.TryAdd(alias, target);
        }

        return new ResolutionContext(NamespaceOf(decl), containing, usings, aliases);
    }

    private static void AddUsings(
        IEnumerable<UsingDirectiveSyntax> directives, List<string> usings, Dictionary<string, string> aliases)
    {
        var named = directives
            .Where(directive => !directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            .Select(directive => (directive.Alias, Target: directive.Name is { } name ? Dotted(name)?.Text : null))
            .Where(directive => directive.Target is not null);
        foreach (var (alias, target) in named)
        {
            if (alias is not null)
                aliases.TryAdd(alias.Name.Identifier.ValueText, target!);
            else
                usings.Add(target!);
        }
    }

    private static Dictionary<string, UsingSet> CollectGlobalUsings(
        List<(string Path, CompilationUnitSyntax Root)> parsed)
    {
        var result = new Dictionary<string, UsingSet>(StringComparer.Ordinal);
        foreach (var (path, root) in parsed)
        {
            var globals = root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToList();
            if (globals.Count == 0)
                continue;

            var project = ProjectKey(path);
            if (!result.TryGetValue(project, out var set))
            {
                set = new UsingSet();
                result.Add(project, set);
            }

            AddUsings(globals, set.Namespaces, set.Aliases);
        }

        return result;
    }

    /// <summary>
    /// The project a file belongs to, for global usings: the segment after <c>Tests/</c>
    /// (<c>Tests/Verbara.Sdk.Ari.Tests/…</c> → <c>Verbara.Sdk.Ari.Tests</c>), or the file's directory.
    /// </summary>
    private static string ProjectKey(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        var tests = Array.IndexOf(segments, "Tests");
        if (tests >= 0 && tests + 2 < segments.Length)
            return segments[tests + 1];

        return string.Join('/', segments[..^1]);
    }

    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => string.Empty,
        };
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name[..^"Attribute".Length];

        return name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
    }

    private sealed class UsingSet
    {
        public List<string> Namespaces { get; } = [];

        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ResolutionContext(
        string Namespace,
        IReadOnlyList<string> ContainingTypes,
        IReadOnlyList<string> Usings,
        IReadOnlyDictionary<string, string> Aliases);

    private sealed record TypePart(TypeDeclarationSyntax Declaration, string Path, ResolutionContext Context)
    {
        public int Line => Declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    private sealed class TypeInfo(string key)
    {
        public string Key { get; } = key;

        public List<TypePart> Parts { get; } = [];

        public bool IsClass => Parts.TrueForAll(p =>
            p.Declaration is ClassDeclarationSyntax
            || (p.Declaration is RecordDeclarationSyntax r && !r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)));

        public bool IsAbstract => Parts.Exists(p => p.Declaration.Modifiers.Any(SyntaxKind.AbstractKeyword));

        public bool HasOwnTestMethod => Parts.Exists(p =>
            p.Declaration.Members.OfType<MethodDeclarationSyntax>().Any(m =>
                m.AttributeLists.SelectMany(l => l.Attributes).Any(IsTestAttribute)));
    }
}
