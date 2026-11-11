using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A <c>try</c> guarded by a decode catch that holds more than the decode.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based and points at the <c>try</c> keyword.
/// </summary>
internal sealed record ReceiveLoopCatchScopeViolation(string Path, int Line, string Reason);

/// <summary>
/// Pure, I/O-free detector for "a receive loop's decode catch covers the decode and nothing after it":
/// every <c>try</c> one of whose <c>catch</c> clauses names <c>JsonException</c> or
/// <c>FormatException</c> holds <b>exactly one statement</b>, and that statement's <b>only invocation
/// expression</b> is <c>JsonSerializer.Deserialize(…)</c>, <c>JsonDocument.Parse(…)</c> or
/// <c>Convert.FromBase64String(…)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A receive loop turns a message it cannot read into a counted skip, and a vendor failure named in an
/// unreadable envelope into the provider failure. That recovery is for <em>reading</em> the vendor's
/// message. Anything wider absorbs exceptions it was never meant to see: a write to the caller's
/// channel, an audio write, a published event or a registered handler that throws a
/// <c>JsonException</c> of its own would be counted as an unparseable frame and skipped, and a
/// consumer's failure would vanish into a metric. A test that sends a malformed message fails inside
/// the decode and never reaches that boundary, so only a structural rule can hold it.
/// </para>
/// <para>
/// Accepted shapes: <c>v = X(…);</c>, <c>T v = X(…);</c>, <c>using var d = X(…);</c>,
/// <c>return X(…);</c> and a bare <c>X(…);</c>. Rejected: a <c>try</c> of two statements or more, and
/// one whose statement makes a second invocation — <c>writer.WriteAsync(X(…))</c> is two. A member
/// access such as <c>Context.Default.Type</c> or <c>buffer.WrittenSpan</c> is not an invocation and does
/// not count. The decode call is recognised by its method name on <c>JsonSerializer</c>,
/// <c>JsonDocument</c> or <c>Convert</c>, unqualified or namespace-qualified, generic or not.
/// </para>
/// <para>
/// Syntax only. Property getters, operators and constructors are not invocations and are not seen; a
/// <c>catch (Exception)</c> is not a decode catch and is not judged here. The rule says what the
/// <c>try</c> covers, not what the <c>catch</c> does: whether an unreadable message is counted, peeked
/// or reported stays with each provider's own tests.
/// </para>
/// </remarks>
internal static class ReceiveLoopCatchScopeScanner
{
    /// <summary>The exceptions that mark a decode catch.</summary>
    public static readonly IReadOnlySet<string> DecodeExceptions =
        new HashSet<string>(StringComparer.Ordinal) { "JsonException", "FormatException" };

    /// <summary>The decode calls such a <c>try</c> may hold, as (receiver type, method).</summary>
    public static readonly IReadOnlyList<(string Receiver, string Method)> DecodeCalls =
    [
        ("JsonSerializer", "Deserialize"),
        ("JsonDocument", "Parse"),
        ("Convert", "FromBase64String"),
    ];

    /// <summary>The decode-guarded <c>try</c> statements in <paramref name="source"/> that break the rule.</summary>
    public static IReadOnlyList<ReceiveLoopCatchScopeViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var violations = new List<ReceiveLoopCatchScopeViolation>();
        foreach (var guarded in GuardedTries(source))
        {
            var reason = Judge(guarded.Block);
            if (reason is null) continue;

            var line = guarded.TryKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new ReceiveLoopCatchScopeViolation(path, line, reason));
        }

        return violations;
    }

    /// <summary>
    /// Counts the decode-guarded <c>try</c> statements in <paramref name="source"/>, compliant or not. The
    /// guard's liveness floor sums it over the scanned files: a detector that stopped recognising them
    /// would otherwise pass having judged nothing.
    /// </summary>
    public static int CountGuardedTries(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GuardedTries(source).Count();
    }

    private static IEnumerable<TryStatementSyntax> GuardedTries(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TryStatementSyntax>()
            .Where(statement => statement.Catches.Any(IsDecodeCatch));

    private static bool IsDecodeCatch(CatchClauseSyntax clause) =>
        clause.Declaration?.Type is { } type && DecodeExceptions.Contains(RightmostName(type));

    /// <summary>Why <paramref name="block"/> breaks the rule, or <see langword="null"/> when it holds.</summary>
    private static string? Judge(BlockSyntax block)
    {
        if (block.Statements.Count != 1)
            return $"the try holds {block.Statements.Count} statements; a decode catch covers one, the decode";

        var invocations = block.Statements[0].DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToList();
        if (invocations.Count != 1)
        {
            var calls = string.Join(", ", invocations.Select(invocation => invocation.Expression.ToString()));
            return $"the statement makes {invocations.Count} invocations ({calls}); only the decode call may be inside the try";
        }

        return IsDecodeCall(invocations[0])
            ? null
            : $"the only invocation is {invocations[0].Expression}, not JsonSerializer.Deserialize, JsonDocument.Parse or Convert.FromBase64String";
    }

    private static bool IsDecodeCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access) return false;

        var method = access.Name.Identifier.Text;
        var receiver = ReceiverName(access.Expression);
        return DecodeCalls.Any(call => call.Method == method && call.Receiver == receiver);
    }

    /// <summary><c>JsonSerializer</c> and <c>System.Text.Json.JsonSerializer</c> both reduce to the type's simple name.</summary>
    private static string? ReceiverName(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>The simple name of a type, without namespace qualification or type arguments.</summary>
    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => RightmostName(qualified.Right),
        AliasQualifiedNameSyntax alias => RightmostName(alias.Name),
        GenericNameSyntax generic => generic.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => string.Empty,
    };
}
