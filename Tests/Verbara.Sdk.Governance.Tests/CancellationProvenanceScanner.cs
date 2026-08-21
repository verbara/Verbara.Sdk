using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A cancellation test that hands the cancelled token to the enumerator as well as to the subject.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record CancellationProvenanceViolation(string Path, int Line, string Method, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports cancellation tests whose
/// assertion cannot distinguish the subject throwing from the enumerator throwing (ADR-0052 F3).
/// </summary>
/// <remarks>
/// <para>
/// <c>ToListAsync(ct)</c>, <c>ToArrayAsync(ct)</c> and <c>WithCancellation(ct)</c> all check the
/// token themselves at every iteration boundary. Give one of them the same token the test cancels
/// and the <c>OperationCanceledException</c> the assertion catches may have come from the enumerator
/// — so a subject that swallows the token and ends its sequence with a silent <c>yield break</c>
/// passes identically to one that propagates. That is not a hypothetical: all ten cancellation tests
/// in the VoiceAi suites were written this way, seven of them over code that happened to be correct,
/// and none of them could demonstrate it. The token goes to the subject; the enumeration takes
/// <c>CancellationToken.None</c> or no argument at all.
/// </para>
/// <para>
/// A method is a cancellation test if it cancels a source inside itself — <c>Cancel()</c>,
/// <c>CancelAsync()</c> or <c>CancelAfter(…)</c>, including from a nested lambda, which is how the
/// cancel-on-first-frame triggers are written. Nothing keys on the <c>[Fact]</c> attribute or on the
/// method name, so a helper with the same shape is reported too.
/// </para>
/// <para>
/// A <c>CancellationTokenSource</c> constructed with a delay is deliberately NOT treated as
/// cancelled. In a test that is not about cancellation it is a hang bound, and reporting it would
/// mute the detector on the legitimate case; the prohibition on racing a wall-clock timer against a
/// fake server is the sync-fence ratchet's job, not this one's.
/// </para>
/// <para>
/// Detection is syntactic, so C# snippets embedded as ordinary string literals — this detector's own
/// test fixtures included — can never self-flag.
/// </para>
/// </remarks>
internal static class CancellationProvenanceScanner
{
    /// <summary>Enumeration helpers that check the token themselves at every iteration boundary.</summary>
    private static readonly HashSet<string> EnumeratingMethods = new(StringComparer.Ordinal)
    {
        "ToListAsync", "ToArrayAsync", "WithCancellation",
    };

    /// <summary>Calls that make a source's token fire under the test's own control.</summary>
    private static readonly HashSet<string> CancellingMethods = new(StringComparer.Ordinal)
    {
        "Cancel", "CancelAsync", "CancelAfter",
    };

    public static IReadOnlyList<CancellationProvenanceViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<CancellationProvenanceViolation>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var cancelledSources = CollectCancelledSources(method);
            if (cancelledSources.Count == 0)
                continue;

            var cancelledTokens = CollectCancelledTokenAliases(method, cancelledSources);

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access)
                    continue;
                if (!EnumeratingMethods.Contains(access.Name.Identifier.Text))
                    continue;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (!IsCancelledToken(argument.Expression, cancelledSources, cancelledTokens))
                        continue;

                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add(new CancellationProvenanceViolation(
                        path,
                        line,
                        method.Identifier.Text,
                        $"'{access.Name.Identifier.Text}' receives the same token the test cancels, so it " +
                        "throws OperationCanceledException whether or not the subject does — a silent " +
                        "'yield break' passes this assertion identically to a propagated throw. Pass the " +
                        "token to the subject only; enumerate with CancellationToken.None."));
                    break;
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Number of enumeration sites in <paramref name="source"/>, cancelled or not. The guard's
    /// liveness self-test asserts on this: if the suites move to a different enumeration helper, the
    /// detector goes quiet without failing anything.
    /// </summary>
    public static int CountEnumerationSites(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(i => i.Expression is MemberAccessExpressionSyntax access
                && EnumeratingMethods.Contains(access.Name.Identifier.Text));
    }

    /// <summary>
    /// Names of the sources this method cancels. Nested lambdas count: the cancel-on-first-frame
    /// trigger is written as <c>Task.Run(async () =&gt; … await cts.CancelAsync())</c>.
    /// </summary>
    private static HashSet<string> CollectCancelledSources(MethodDeclarationSyntax method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access)
                continue;
            if (!CancellingMethods.Contains(access.Name.Identifier.Text))
                continue;

            var target = SimpleName(access.Expression);
            if (target is not null)
                names.Add(target);
        }

        return names;
    }

    /// <summary>
    /// Locals that alias a cancelled source's token (<c>var token = cts.Token;</c>), so renaming the
    /// token on the way to the enumerator does not hide the defect.
    /// </summary>
    private static HashSet<string> CollectCancelledTokenAliases(
        MethodDeclarationSyntax method,
        HashSet<string> cancelledSources)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declarator in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not { } value)
                continue;
            if (IsSourceToken(value, cancelledSources))
                names.Add(declarator.Identifier.Text);
        }

        return names;
    }

    private static bool IsCancelledToken(
        ExpressionSyntax expression,
        HashSet<string> cancelledSources,
        HashSet<string> cancelledTokens)
    {
        if (IsSourceToken(expression, cancelledSources))
            return true;

        return expression is IdentifierNameSyntax identifier
            && cancelledTokens.Contains(identifier.Identifier.Text);
    }

    /// <summary>
    /// True for <c>&lt;cancelled&gt;.Token</c>. <c>CancellationToken.None</c> has the same shape but
    /// its receiver is never a source the method cancelled, so it can never match.
    /// </summary>
    private static bool IsSourceToken(ExpressionSyntax expression, HashSet<string> cancelledSources)
    {
        if (expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Token" } access)
            return false;

        var receiver = SimpleName(access.Expression);
        return receiver is not null && cancelledSources.Contains(receiver);
    }

    /// <summary>Reduces <c>x</c> and <c>this.x</c> to <c>x</c>; anything else to <see langword="null"/>.</summary>
    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } =>
            name.Identifier.Text,
        _ => null,
    };
}
