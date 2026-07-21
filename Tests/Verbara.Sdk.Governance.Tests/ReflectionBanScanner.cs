using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A single AOT-breaking reflection site found in product source that lacks a valid inline
/// <c>// aot-allow:</c> marker. <see cref="Path"/> is a display path (repo-relative when produced
/// by the tree scan), <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record ReflectionViolation(string Path, int Line, string Api, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports the reflection APIs that break
/// Native AOT (ADR-0022): non-generic <c>Activator.CreateInstance</c>, static <c>Type.GetType</c>,
/// <c>MakeGenericType</c>/<c>MakeGenericMethod</c>, <c>new DynamicMethod</c>, and
/// <c>System.Reflection.Emit</c> imports — none of which the trimmer/AOT compiler can resolve
/// statically. Detection is syntactic (real invocation/object-creation/using nodes), so comment,
/// XML-doc, and string-literal mentions can never produce a false positive. The AOT-safe generic
/// forms (<c>Activator.CreateInstance&lt;T&gt;()</c>, instance <c>x.GetType()</c>) are intentionally
/// NOT flagged. Sites that are genuinely safe (source-gen shims, verified interop) may be excused
/// with an inline <c>// aot-allow:</c> marker.
/// </summary>
internal static class ReflectionBanScanner
{
    /// <summary>
    /// Valid allow-marker: literal <c>// aot-allow:</c>, a category from the CLOSED enum, an
    /// em-dash (<c>—</c>) or <c>--</c> separator, then at least one non-space char (the reason).
    /// A bare marker, an unknown category, or an empty reason is NOT valid.
    /// </summary>
    private static readonly Regex AllowMarker = new(
        @"//\s*aot-allow:\s*(SOURCE-GEN|INTEROP|VERIFIED-SAFE)\s*(?:—|--)\s*\S",
        RegexOptions.None);

    public static IReadOnlyList<ReflectionViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var violations = new List<ReflectionViolation>();

        // Valid allow-markers are REAL single-line comment trivia (never string/XML-doc text).
        // We collect their 0-based start lines so a marker only excuses a node on its own line
        // span or the single immediately-preceding physical line.
        var markerLines = new HashSet<int>();
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && AllowMarker.IsMatch(trivia.ToString()))
            {
                markerLines.Add(trivia.GetLocation().GetLineSpan().StartLinePosition.Line);
            }
        }

        // (a) Banned invocations: Activator.CreateInstance (non-generic), Type.GetType (static),
        //     MakeGenericType, MakeGenericMethod.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax mae)
                continue;

            var (api, detail) = ClassifyInvocation(mae);
            if (api is null)
                continue;

            if (IsAllowed(invocation, markerLines))
                continue;

            var line = LineSpan(invocation).StartLinePosition.Line + 1;
            violations.Add(new ReflectionViolation(path, line, api, detail!));
        }

        // (b) Banned object creation: new DynamicMethod(...).
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (TrailingTypeName(creation.Type) != "DynamicMethod")
                continue;

            if (IsAllowed(creation, markerLines))
                continue;

            var line = LineSpan(creation).StartLinePosition.Line + 1;
            violations.Add(new ReflectionViolation(
                path,
                line,
                "Reflection.Emit.DynamicMethod",
                "AOT-breaking: 'new DynamicMethod(...)' emits IL at runtime — not resolvable by the AOT compiler; use a source generator or annotate."));
        }

        // (c) Banned import: using [static] …System.Reflection.Emit.
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Name is null)
                continue;

            if (!directive.Name.ToString().EndsWith("System.Reflection.Emit", StringComparison.Ordinal))
                continue;

            if (IsAllowed(directive, markerLines))
                continue;

            var line = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new ReflectionViolation(
                path,
                line,
                "using System.Reflection.Emit",
                "AOT-breaking: 'System.Reflection.Emit' generates IL at runtime — banned in shippable AOT code; use a source generator or annotate."));
        }

        return violations;
    }

    /// <summary>
    /// Classifies a member-access invocation as one of the banned reflection APIs, or returns a
    /// <c>null</c> api when it is not banned. Only the AOT-breaking forms match: non-generic
    /// <c>Activator.CreateInstance</c> (the generic overload is AOT-safe), <c>Type.GetType</c>
    /// where the receiver is the literal static class <c>Type</c> (instance <c>x.GetType()</c> is
    /// AOT-safe), and <c>MakeGenericType</c>/<c>MakeGenericMethod</c> on any receiver.
    /// </summary>
    private static (string? Api, string? Detail) ClassifyInvocation(MemberAccessExpressionSyntax mae)
    {
        var method = mae.Name.Identifier.Text;
        var receiver = TrailingReceiver(mae.Expression);

        switch (method)
        {
            case "CreateInstance"
                when receiver == "Activator" && mae.Name is IdentifierNameSyntax:
                // Non-generic Activator.CreateInstance(Type/...); the generic
                // Activator.CreateInstance<T>() (Name is GenericNameSyntax) is AOT-safe.
                return (
                    "Activator.CreateInstance",
                    "AOT-breaking: non-generic 'Activator.CreateInstance(...)' constructs a type the trimmer cannot see; use the generic 'Activator.CreateInstance<T>()' or a source generator, or annotate.");

            case "GetType"
                when receiver == "Type":
                // Static Type.GetType("name"); instance x.GetType() (receiver not literal "Type")
                // is AOT-safe.
                return (
                    "Type.GetType",
                    "AOT-breaking: static 'Type.GetType(...)' resolves a type by name at runtime — not resolvable by the AOT compiler; use typeof/generics or annotate.");

            case "MakeGenericType":
                return (
                    "Type.MakeGenericType",
                    "AOT-breaking: 'MakeGenericType(...)' constructs a generic type at runtime — not resolvable by the AOT compiler; use static generics or annotate.");

            case "MakeGenericMethod":
                return (
                    "MethodInfo.MakeGenericMethod",
                    "AOT-breaking: 'MakeGenericMethod(...)' constructs a generic method at runtime — not resolvable by the AOT compiler; use static generics or annotate.");

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Trailing identifier of the receiver expression: handles both <c>Type.GetType(...)</c>
    /// (receiver is an <see cref="IdentifierNameSyntax"/>) and the fully-qualified
    /// <c>System.Type.GetType(...)</c> (receiver is itself a member access whose trailing name is
    /// <c>Type</c>).
    /// </summary>
    private static string? TrailingReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>
    /// Trailing simple-name of a type reference: handles both <c>new DynamicMethod(...)</c>
    /// (an <see cref="IdentifierNameSyntax"/>) and the qualified
    /// <c>new System.Reflection.Emit.DynamicMethod(...)</c> (a <see cref="QualifiedNameSyntax"/>
    /// whose right-most name is <c>DynamicMethod</c>).
    /// </summary>
    private static string? TrailingTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        _ => null,
    };

    private static FileLinePositionSpan LineSpan(SyntaxNode node) =>
        node.GetLocation().GetLineSpan();

    /// <summary>
    /// A flagged node is allowed iff a valid marker comment appears on any line the node spans, or
    /// on the single IMMEDIATELY-preceding physical line. Blank lines are not skipped: a marker
    /// above a blank line does not excuse a node below that blank line.
    /// </summary>
    private static bool IsAllowed(SyntaxNode node, HashSet<int> markerLines)
    {
        var span = LineSpan(node);
        var startLine = span.StartLinePosition.Line;
        var endLine = span.EndLinePosition.Line;

        for (var i = startLine; i <= endLine; i++)
        {
            if (markerLines.Contains(i))
                return true;
        }

        return markerLines.Contains(startLine - 1);
    }
}
