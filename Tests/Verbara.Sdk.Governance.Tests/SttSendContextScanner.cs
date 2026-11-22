using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A use of the STT package's strict read options outside a read. <see cref="Path"/> is a display
/// path (repo-relative when produced by the tree scan), <see cref="Line"/> is 1-based and points at
/// the <c>SttJsonReadOptions</c> reference.
/// </summary>
internal sealed record SttSendContextViolation(string Path, int Line, string Reason);

/// <summary>
/// Judges where <c>SttJsonReadOptions</c> may appear in the STT package: only inside the argument list
/// of a <c>JsonSerializer.Deserialize*</c> call. The strict options set
/// <c>RespectNullableAnnotations</c>, which also makes a write throw on a null non-nullable member, so
/// an outbound request that reached them would change from sending <c>null</c> to throwing. Two shapes
/// are reported: a <c>Serialize*</c> call that names the options in its arguments, and any other use
/// (a field, a local, a helper) that could carry them to a send without naming them there.
/// Comments and strings are not syntax nodes, so naming the type in either reports nothing.
/// </summary>
internal static class SttSendContextScanner
{
    public const string ReadOptionsType = "SttJsonReadOptions";

    /// <summary>The violations in <paramref name="source"/>, in source order.</summary>
    public static IReadOnlyList<SttSendContextViolation> Scan(string source, string path, string readOptionsType = ReadOptionsType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(readOptionsType);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<SttSendContextViolation>();
        foreach (var reference in ReadOptionsReferences(root, readOptionsType))
        {
            var line = reference.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var call = EnclosingSerializerCall(reference);
            if (call is null)
            {
                violations.Add(new SttSendContextViolation(path, line,
                    "used outside a JsonSerializer call's arguments, where it can reach a send unnamed"));
            }
            else if (!SerializerMethod(call)!.StartsWith("Deserialize", StringComparison.Ordinal))
            {
                violations.Add(new SttSendContextViolation(path, line,
                    $"passed to JsonSerializer.{SerializerMethod(call)}: sends must use the plain generated context"));
            }
        }

        return violations;
    }

    /// <summary>
    /// The number of <c>JsonSerializer.Serialize*</c> calls in <paramref name="source"/>: the send sites
    /// the guard protects. The tree test's liveness floor asserts on it.
    /// </summary>
    public static int CountSerializeCalls(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(call => SerializerMethod(call)?.StartsWith("Serialize", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The number of <c>JsonSerializer.Deserialize*</c> calls in <paramref name="source"/> that read
    /// through the strict options: the fenced read sites.
    /// </summary>
    public static int CountStrictReads(string source) => CountStrictReads(source, ReadOptionsType);

    public static int CountStrictReads(string source, string readOptionsType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readOptionsType);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return ReadOptionsReferences(root, readOptionsType)
            .Select(EnclosingSerializerCall)
            .Count(call => call is not null &&
                SerializerMethod(call)!.StartsWith("Deserialize", StringComparison.Ordinal));
    }

    private static IEnumerable<IdentifierNameSyntax> ReadOptionsReferences(SyntaxNode root, string readOptionsType) =>
        root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == readOptionsType);

    /// <summary>
    /// The innermost <c>JsonSerializer.X(…)</c> invocation whose argument list contains
    /// <paramref name="node"/>, or <see langword="null"/>.
    /// </summary>
    private static InvocationExpressionSyntax? EnclosingSerializerCall(SyntaxNode node)
    {
        foreach (var argumentList in node.Ancestors().OfType<ArgumentListSyntax>())
        {
            if (argumentList.Parent is InvocationExpressionSyntax call && SerializerMethod(call) is not null)
                return call;
        }

        return null;
    }

    /// <summary>The method name when <paramref name="call"/> is <c>[qualified.]JsonSerializer.X(…)</c>.</summary>
    private static string? SerializerMethod(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax access && ReceiverIsJsonSerializer(access.Expression)
            ? access.Name.Identifier.Text
            : null;

    private static bool ReceiverIsJsonSerializer(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == "JsonSerializer",
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text == "JsonSerializer",
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text == "JsonSerializer",
        _ => false,
    };
}
