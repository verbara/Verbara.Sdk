using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A place in a VoiceAi package that can make a provider reply with an unknown member fail to read.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan), <see cref="Line"/>
/// is 1-based, <see cref="Form"/> names the shape found.
/// </summary>
internal sealed record UnmappedMemberViolation(string Path, int Line, string Form);

/// <summary>
/// Reports every way a VoiceAi package can turn the tolerate-unknown-members default off: the
/// <c>JsonUnmappedMemberHandling.Disallow</c> value in any of its forms — a context-wide
/// <c>[JsonSourceGenerationOptions(UnmappedMemberHandling = …)]</c>, a type-level
/// <c>[JsonUnmappedMemberHandling(…)]</c>, a <c>JsonSerializerOptions</c> initialized or assigned with it,
/// or the value held anywhere else, from where it can reach one of those — plus the two spellings that
/// hide the value's name: a cast to <c>JsonUnmappedMemberHandling</c> and a <c>using</c> directive (static
/// or alias) that names the enum.
/// </summary>
/// <remarks>
/// Syntactic, through Roslyn: comments, XML docs and string literals are not syntax nodes, so naming the
/// value in prose reports nothing. The limit is the one every text scanner has: it proves which value a
/// line of source names, not what an options object does at runtime — an options object built elsewhere
/// and handed in, or a value read from configuration, is invisible to it. What the parser does with an
/// unknown member is the behaviour tests' job.
/// </remarks>
internal static class UnmappedMemberHandlingScanner
{
    private const string EnumName = "JsonUnmappedMemberHandling";

    /// <summary>
    /// The package folders the guard reads: every <c>src/Verbara.Sdk.VoiceAi*</c> package, so a provider
    /// package added later is in scope without an edit here. AMI, ARI, AGI and Sessions are not
    /// third-party wire contracts and stay out.
    /// </summary>
    public const string ScopePrefix = "src/Verbara.Sdk.VoiceAi";

    /// <summary>Whether the repo-relative, '/'-separated <paramref name="relativePath"/> is in the guard's scope.</summary>
    public static bool IsInScope(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (!relativePath.StartsWith(ScopePrefix, StringComparison.Ordinal)) return false;

        // The package folder itself, or one of its dotted siblings — never a folder that merely shares the prefix.
        var rest = relativePath.AsSpan(ScopePrefix.Length);
        return rest.StartsWith("/", StringComparison.Ordinal) || rest.StartsWith(".", StringComparison.Ordinal);
    }

    /// <summary>The violations in <paramref name="source"/>, in source order.</summary>
    public static IReadOnlyList<UnmappedMemberViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<(SyntaxNode Node, string Form)>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax access
                    when access.Name.Identifier.Text == "Disallow" && TrailingName(access.Expression) == EnumName:
                    violations.Add((access, FormOf(access)));
                    break;

                case CastExpressionSyntax cast when TrailingName(cast.Type) == EnumName:
                    violations.Add((cast, $"a cast to {EnumName}, which hides which value it is"));
                    break;

                case UsingDirectiveSyntax directive
                    when directive.Name is { } name && TrailingName(name) == EnumName
                        && (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || directive.Alias is not null):
                    violations.Add((directive,
                        $"a using directive naming {EnumName}, which lets Disallow appear without the enum's name"));
                    break;
            }
        }

        return violations
            .Select(v => new UnmappedMemberViolation(path, v.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, v.Form))
            .ToList();
    }

    /// <summary>The number of <c>JsonSerializerContext</c> subclasses declared in <paramref name="source"/>.</summary>
    public static int CountSerializerContexts(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Count(type => type.BaseList?.Types.Any(b => TrailingName(b.Type) == "JsonSerializerContext") == true);
    }

    /// <summary>The number of <c>new JsonSerializerOptions(…)</c> constructions in <paramref name="source"/>.</summary>
    public static int CountOptionsConstructions(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Count(creation => TrailingName(creation.Type) == "JsonSerializerOptions");
    }

    private static string FormOf(MemberAccessExpressionSyntax access)
    {
        foreach (var ancestor in access.Ancestors())
        {
            switch (ancestor)
            {
                case AttributeSyntax attribute when AttributeName(attribute) == "JsonSourceGenerationOptions":
                    return "[JsonSourceGenerationOptions(UnmappedMemberHandling = Disallow)] on a context: every type it reads";
                case AttributeSyntax attribute when AttributeName(attribute) == EnumName:
                    return "[JsonUnmappedMemberHandling(Disallow)] on a type: every reply that type reads";
                case AssignmentExpressionSyntax assignment when TrailingName(assignment.Left) == "UnmappedMemberHandling":
                    return "UnmappedMemberHandling = Disallow on a JsonSerializerOptions: every read through it";
                case StatementSyntax or MemberDeclarationSyntax:
                    break;
                default:
                    continue;
            }

            break;
        }

        return $"{EnumName}.Disallow held where it can reach an options object or an attribute";
    }

    private static string? AttributeName(AttributeSyntax attribute)
    {
        var name = TrailingName(attribute.Name);
        return name is not null && name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static string? TrailingName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        _ => null,
    };
}
