using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Pure, I/O-free detector for ADR-0059 R2: a hosted service does not keep the token the host hands
/// to <c>StartAsync</c>. Reports each assignment inside a hosted service's <c>StartAsync</c> that
/// stores that token in a field, or stores a <c>CancellationTokenSource.CreateLinkedTokenSource</c>
/// over it, as a <see cref="DecisionGuardSite"/> keyed by the text of the assignment, so the guard can
/// match it against <c>decision-guards-baseline.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>IHostedService.StartAsync</c>'s token means "the start was aborted". The host links it into a
/// source it releases the moment the start returns, so after a completed start the token can never
/// be cancelled again, and <c>Register</c> on it is accepted and silently never fires (measured in
/// ADR-0059). A service that keeps it has a lifetime token nothing will ever cancel; one that links
/// it into a source it keeps couples every later use to an aborted start. R2 forbids both: the
/// parameter is used within its phase and replaced, not stored and not combined.
/// </para>
/// <para>
/// <b>A hosted service</b> is a type whose base list names <c>IHostedService</c>,
/// <c>IHostedLifecycleService</c> or <c>BackgroundService</c>, qualified or not, on the declaration
/// that holds <c>StartAsync</c> or on another part of the same partial type in the same file. Its
/// <c>StartAsync</c> is the method of that name declared directly in it (explicit interface
/// implementations included) with a <c>CancellationToken</c> parameter; nested types are judged on
/// their own base list. A <c>StartAsync</c> on any other type is not this rule's concern: whether R2
/// also binds components that are not hosted services is an open ruling, and this guard does not
/// decide it.
/// </para>
/// <para>
/// <b>Kept</b> means an assignment anywhere in that method's body — including inside a lambda or
/// local function it declares, such as an event handler that runs after the start returned — whose
/// right side is the token parameter itself, or a <c>CreateLinkedTokenSource(…)</c> call with the
/// token among its arguments (or that call's <c>.Token</c>), looked through parentheses, casts,
/// <c>!</c> and the branches of a conditional; and whose target is not local to the method: a bare
/// name the method does not declare, <c>this.</c>/<c>base.</c> anything, or a member or element of
/// something the method does not declare. A local holding the token, a local linked source disposed
/// within the start, the token passed to a call, and an object initializer's <c>Token = …</c> are
/// uses within the phase and pass. A lambda parameter that shadows the token's name is not the token.
/// </para>
/// <para>
/// The rule reaches the <b>first hop</b> only, which is what the spec states. A token copied into a
/// local and the local then stored, or handed to a constructor, a method or a component that keeps it
/// — the shape ADR-0059 itself fixed, <c>SetShutdownToken(cancellationToken)</c> — is out of reach of
/// a syntactic scan that cannot see what the callee does with it. So is a hosted service whose
/// interface arrives through a base class of its own, and a partial part declared in another file.
/// </para>
/// <para>
/// Detection is syntactic (real assignment nodes), so a mention in a comment, an XML doc or a string
/// literal — this scanner's own fixtures included — can never produce a site.
/// </para>
/// </remarks>
internal static class HostedStartTokenScanner
{
    /// <summary>The name this guard's entries carry in <c>decision-guards-baseline.json</c>.</summary>
    public const string GuardName = "hosted-service-start-token";

    /// <summary>
    /// Base-list names that make a type a hosted service. <c>IHostedLifecycleService</c> extends
    /// <c>IHostedService</c>, and <c>BackgroundService</c> implements it with a virtual
    /// <c>StartAsync</c> a subclass may override.
    /// </summary>
    private static readonly HashSet<string> HostedServiceBaseNames = new(StringComparer.Ordinal)
    {
        "IHostedService", "IHostedLifecycleService", "BackgroundService",
    };

    /// <summary>
    /// The assignments in <paramref name="source"/> that keep a hosted service's <c>StartAsync</c>
    /// token. <see cref="DecisionGuardSite.Key"/> is the assignment's text.
    /// </summary>
    public static IReadOnlyList<DecisionGuardSite> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var sites = new List<DecisionGuardSite>();
        foreach (var (method, tokenNames) in HostedStartMethods(source))
        {
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body is null)
                continue;

            var declared = DeclaredNames(method);
            var carriers = body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.Parent is not InitializerExpressionSyntax
                    && IsState(assignment.Left, declared)
                    && CarriesToken(assignment.Right, tokenNames));
            foreach (var assignment in carriers)
            {
                sites.Add(new DecisionGuardSite(
                    path,
                    assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    assignment.ToString()));
            }
        }

        return sites;
    }

    /// <summary>
    /// Counts the hosted-service <c>StartAsync</c> methods in <paramref name="source"/> that take a
    /// token, compliant or not. The guard's liveness floor sums it over the tree: once the known sites
    /// are fixed the baseline is empty, and a detector that stopped recognising hosted services would
    /// then pass without inspecting one.
    /// </summary>
    public static int CountHostedStartMethods(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return HostedStartMethods(source).Count();
    }

    private static IEnumerable<(MethodDeclarationSyntax Method, HashSet<string> TokenNames)> HostedStartMethods(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

        // A partial type may carry its base list on one part and StartAsync on another; parts in the
        // same file are joined by their qualified name.
        var hostedPartials = types
            .Where(t => t.Modifiers.Any(SyntaxKind.PartialKeyword) && NamesAHostedServiceBase(t))
            .Select(QualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in types)
        {
            var hosted = NamesAHostedServiceBase(type)
                || (type.Modifiers.Any(SyntaxKind.PartialKeyword) && hostedPartials.Contains(QualifiedName(type)));
            if (!hosted)
                continue;

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == "StartAsync"))
            {
                var tokenNames = method.ParameterList.Parameters
                    .Where(p => p.Type is not null && TrailingName(p.Type) == "CancellationToken")
                    .Select(p => p.Identifier.ValueText)
                    .ToHashSet(StringComparer.Ordinal);
                if (tokenNames.Count > 0)
                    yield return (method, tokenNames);
            }
        }
    }

    private static bool NamesAHostedServiceBase(TypeDeclarationSyntax type) =>
        type.BaseList?.Types.Any(b => TrailingName(b.Type) is { } name && HostedServiceBaseNames.Contains(name)) == true;

    /// <summary>The type's name qualified by its enclosing types and namespaces, to join partial parts.</summary>
    private static string QualifiedName(TypeDeclarationSyntax type)
    {
        var parts = new List<string> { type.Identifier.ValueText };
        for (var node = type.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax outer:
                    parts.Add(outer.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>
    /// Every name <paramref name="method"/> declares — parameters, locals, pattern and <c>out</c>
    /// variables, <c>foreach</c> and <c>catch</c> variables, and the parameters of the lambdas and
    /// local functions inside it. An assignment target rooted at one of them is local to the start.
    /// </summary>
    private static HashSet<string> DeclaredNames(MethodDeclarationSyntax method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in method.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    names.Add(parameter.Identifier.ValueText);
                    break;
                case VariableDeclaratorSyntax variable:
                    names.Add(variable.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    names.Add(designation.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax forEach:
                    names.Add(forEach.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax { Identifier.ValueText: { Length: > 0 } caught }:
                    names.Add(caught);
                    break;
                case LocalFunctionStatementSyntax local:
                    names.Add(local.Identifier.ValueText);
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// Whether an assignment to <paramref name="target"/> outlives the method: its root is
    /// <c>this</c>/<c>base</c>, or a name the method does not declare (a field, a property, a static).
    /// A bare <c>_</c> is a discard, not state.
    /// </summary>
    private static bool IsState(ExpressionSyntax target, HashSet<string> declared)
    {
        var root = target;
        while (true)
        {
            switch (root)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    root = parenthesized.Expression;
                    continue;
                case MemberAccessExpressionSyntax access:
                    root = access.Expression;
                    continue;
                case ElementAccessExpressionSyntax element:
                    root = element.Expression;
                    continue;
                case ConditionalAccessExpressionSyntax conditional:
                    root = conditional.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    root = postfix.Operand;
                    continue;
            }

            break;
        }

        return root switch
        {
            ThisExpressionSyntax or BaseExpressionSyntax => true,
            IdentifierNameSyntax { Identifier.ValueText: "_" } when root == target => false,
            IdentifierNameSyntax id => !declared.Contains(id.Identifier.ValueText),
            _ => false,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> is the start token, or a linked source over it, looked through
    /// parentheses, casts, <c>!</c>, a conditional's branches and a trailing <c>.Token</c> on the
    /// linked source.
    /// </summary>
    private static bool CarriesToken(ExpressionSyntax value, HashSet<string> tokenNames) => value switch
    {
        ParenthesizedExpressionSyntax parenthesized => CarriesToken(parenthesized.Expression, tokenNames),
        CastExpressionSyntax cast => CarriesToken(cast.Expression, tokenNames),
        PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression)
            => CarriesToken(postfix.Operand, tokenNames),
        ConditionalExpressionSyntax conditional
            => CarriesToken(conditional.WhenTrue, tokenNames) || CarriesToken(conditional.WhenFalse, tokenNames),
        IdentifierNameSyntax id => IsStartToken(id, tokenNames),
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Token", Expression: var source }
            => IsLinkedOverToken(source, tokenNames),
        InvocationExpressionSyntax => IsLinkedOverToken(value, tokenNames),
        _ => false,
    };

    /// <summary>
    /// <c>CancellationTokenSource.CreateLinkedTokenSource(…)</c>, qualified or not, with the start token
    /// among its arguments (an array of tokens included).
    /// </summary>
    private static bool IsLinkedOverToken(ExpressionSyntax expression, HashSet<string> tokenNames)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateLinkedTokenSource" } access,
        } invocation
            && TrailingName(access.Expression) == "CancellationTokenSource"
            && invocation.ArgumentList.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => IsStartToken(id, tokenNames));
    }

    /// <summary>
    /// Whether <paramref name="id"/> names the start token: its name is a token parameter's, it is not
    /// the member name of an access (<c>x.cancellationToken</c>), and no lambda or local function
    /// between it and the method redeclares that name as a parameter.
    /// </summary>
    private static bool IsStartToken(IdentifierNameSyntax id, HashSet<string> tokenNames)
    {
        var name = id.Identifier.ValueText;
        if (!tokenNames.Contains(name))
            return false;

        if (id.Parent is MemberAccessExpressionSyntax access && access.Name == id)
            return false;

        for (var node = id.Parent; node is not null and not MethodDeclarationSyntax; node = node.Parent)
        {
            var shadows = node switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText == name,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters.Any(p => p.Identifier.ValueText == name),
                AnonymousMethodExpressionSyntax anonymous => anonymous.ParameterList?.Parameters.Any(p => p.Identifier.ValueText == name) == true,
                LocalFunctionStatementSyntax local => local.ParameterList.Parameters.Any(p => p.Identifier.ValueText == name),
                _ => false,
            };
            if (shadows)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Trailing simple name of a type or receiver: <c>IHostedService</c> for <c>IHostedService</c>,
    /// <c>Microsoft.Extensions.Hosting.IHostedService</c> and <c>global::…IHostedService</c>.
    /// </summary>
    private static string? TrailingName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        QualifiedNameSyntax qualified => TrailingName(qualified.Right),
        AliasQualifiedNameSyntax alias => TrailingName(alias.Name),
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        NullableTypeSyntax nullable => TrailingName(nullable.ElementType),
        _ => null,
    };
}
