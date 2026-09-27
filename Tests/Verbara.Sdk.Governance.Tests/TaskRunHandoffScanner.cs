using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Pure, I/O-free detector for ADR-0058 R2: a <c>Task.Run</c> whose task is discarded passes
/// <c>CancellationToken.None</c> as its own token argument, written explicitly, never a live token.
/// Reports each such call as a <see cref="DecisionGuardSite"/> keyed by the text of the
/// <c>Task.Run(…)</c> invocation, so the guard can match it against
/// <c>decision-guards-baseline.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.Run</c>'s token cancels the work item <i>if it has not yet started</i>, and "started" is
/// decided when the pool picks the item up, not when the caller queues it. A discarded hand-off is
/// the only reference to whatever the work item was meant to own — an accepted connection, a bound
/// listener's accept loop — so a token cancelled in that window drops the work and leaks what it
/// carried, and nothing observes the cancelled task. <c>CA2016</c> does not catch it: it accepts a
/// forwarded token as readily as an explicit <c>None</c>.
/// </para>
/// <para>
/// <b>Discarded</b> means nothing keeps the returned task: <c>_ = Task.Run(…)</c>,
/// <c>var _ = Task.Run(…)</c>, a bare <c>Task.Run(…);</c> statement, and the expression body of a
/// member that returns <c>void</c> (<c>void Start() =&gt; _ = Task.Run(…)</c> is the first form; the
/// body <c>Task.Run(…)</c> of a <c>void</c> method, constructor or <c>set</c>/<c>init</c>/<c>add</c>/
/// <c>remove</c> accessor is the last). Parentheses and a trailing <c>.ConfigureAwait(…)</c> are
/// looked through. An awaited, returned, stored or passed-on task is not discarded: its owner
/// observes the cancellation, so a live token there is not this rule's concern.
/// </para>
/// <para>
/// The token argument is the one named <c>cancellationToken:</c>, else the second positional
/// argument — every <c>Task.Run</c> overload takes <c>(work)</c> or <c>(work, cancellationToken)</c>.
/// A call without one passes: the one-argument overloads run under <c>CancellationToken.None</c>.
/// Anything else is reported, <c>default</c> included, because R2 asks for <c>None</c> written
/// explicitly — the spelling that says "intentionally not propagating the token".
/// </para>
/// <para>
/// One shape is out of reach of a syntactic scan and is not reported: a lambda whose expression body
/// is the <c>Task.Run(…)</c> call itself. Converted to an <c>Action</c> it discards the task; converted
/// to a <c>Func&lt;Task&gt;</c> it returns it, and only the delegate type — which syntax does not
/// carry — tells the two apart. It is treated as returned. <c>() =&gt; _ = Task.Run(…)</c> is
/// reported, because the discard is written.
/// </para>
/// <para>
/// Detection is syntactic (real invocation nodes), so a mention in a comment, an XML doc or a string
/// literal — this scanner's own fixtures included — can never produce a site.
/// </para>
/// </remarks>
internal static class TaskRunHandoffScanner
{
    /// <summary>The name this guard's entries carry in <c>decision-guards-baseline.json</c>.</summary>
    public const string GuardName = "task-run-handoff-token";

    /// <summary>
    /// The discarded <c>Task.Run</c> calls in <paramref name="source"/> whose token argument is not
    /// <c>CancellationToken.None</c>. <see cref="DecisionGuardSite.Key"/> is the invocation's text.
    /// </summary>
    public static IReadOnlyList<DecisionGuardSite> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        return DiscardedTaskRuns(source)
            .Where(invocation => TokenArgument(invocation.ArgumentList) is { } token && !IsExplicitNone(token.Expression))
            .Select(invocation => new DecisionGuardSite(
                path,
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                invocation.ToString()))
            .ToList();
    }

    /// <summary>
    /// Counts every discarded <c>Task.Run</c> in <paramref name="source"/>, compliant or not. The guard's
    /// liveness floor sums it over the tree: once the known sites are fixed the baseline is empty, and
    /// a detector that stopped recognising the hand-off shape would then pass without inspecting one.
    /// </summary>
    public static int CountDiscardedTaskRuns(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return DiscardedTaskRuns(source).Count();
    }

    private static IEnumerable<InvocationExpressionSyntax> DiscardedTaskRuns(string source) =>
        CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsTaskRun(invocation) && IsDiscarded(invocation));

    /// <summary>
    /// <c>Task.Run(…)</c>, <c>Task.Run&lt;T&gt;(…)</c> or any qualified form whose receiver's trailing
    /// name is <c>Task</c> (<c>System.Threading.Tasks.Task.Run</c>, <c>global::…Task.Run</c>).
    /// </summary>
    private static bool IsTaskRun(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Run" } access
        && TrailingName(access.Expression) == "Task";

    private static bool IsDiscarded(InvocationExpressionSyntax invocation)
    {
        SyntaxNode current = invocation;
        while (true)
        {
            if (current.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized;
                continue;
            }

            if (current.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } configure
                && configure.Expression == current
                && configure.Parent is InvocationExpressionSyntax configured)
            {
                current = configured;
                continue;
            }

            break;
        }

        return current.Parent switch
        {
            ExpressionStatementSyntax => true,
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Right == current
                && assignment.Left is IdentifierNameSyntax { Identifier.Text: "_" },
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Identifier.Text: "_" } } => true,
            ArrowExpressionClauseSyntax arrow => ReturnsVoid(arrow.Parent),
            _ => false,
        };
    }

    /// <summary>
    /// Whether the member an expression body belongs to returns nothing, so the body's value is
    /// dropped. A lambda is never one: its body is an <see cref="ExpressionSyntax"/> directly, not an
    /// arrow clause (see the type's remarks).
    /// </summary>
    private static bool ReturnsVoid(SyntaxNode? member) => member switch
    {
        MethodDeclarationSyntax method => IsVoid(method.ReturnType),
        LocalFunctionStatementSyntax local => IsVoid(local.ReturnType),
        ConstructorDeclarationSyntax or DestructorDeclarationSyntax => true,
        AccessorDeclarationSyntax accessor => !accessor.IsKind(SyntaxKind.GetAccessorDeclaration),
        _ => false,
    };

    private static bool IsVoid(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    /// <summary>
    /// The argument <c>Task.Run</c> takes as its token: the one named <c>cancellationToken:</c>, else
    /// the second positional one; <see langword="null"/> for a one-argument call.
    /// </summary>
    private static ArgumentSyntax? TokenArgument(ArgumentListSyntax arguments)
    {
        var named = arguments.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "cancellationToken");
        if (named is not null)
            return named;

        return arguments.Arguments.Count >= 2 && arguments.Arguments[1].NameColon is null
            ? arguments.Arguments[1]
            : null;
    }

    /// <summary>
    /// <c>CancellationToken.None</c>, qualified or not, in parentheses or not. <c>default</c> and
    /// <c>new CancellationToken()</c> carry the same value but not the spelling R2 asks for.
    /// </summary>
    private static bool IsExplicitNone(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "None" } access
            && TrailingName(access.Expression) == "CancellationToken";
    }

    /// <summary>
    /// Trailing simple name of a receiver: <c>Task</c> for <c>Task</c>,
    /// <c>System.Threading.Tasks.Task</c> and <c>global::System.Threading.Tasks.Task</c>.
    /// </summary>
    private static string? TrailingName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        _ => null,
    };
}
