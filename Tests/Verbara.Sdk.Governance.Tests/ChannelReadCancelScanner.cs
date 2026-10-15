using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A channel read in a speech client that is not followed by a re-check of its own token.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan), <see cref="Line"/>
/// is 1-based and points at the loop, <see cref="Token"/> is the token the read was given (empty when
/// it was given none).
/// </summary>
internal sealed record ChannelReadCancelViolation(string Path, int Line, string Client, string Shape, string Token);

/// <summary>A channel read the scanner recognised in a speech client, whether or not it is re-checked.</summary>
internal sealed record ChannelReadSite(string Client, int Line, string Shape);

/// <summary>
/// Pure, I/O-free detector for "a speech client re-checks its token after reading a channel to its
/// end": in every non-abstract <c>SpeechRecognizer</c> or <c>SpeechSynthesizer</c> subclass, each
/// <c>await foreach</c> over a <c>ReadAllAsync(token)</c> call and each
/// <c>while (await reader.WaitToReadAsync(token))</c> loop must be followed, as the next statement of
/// the same block, by <c>token.ThrowIfCancellationRequested();</c> on the same token.
/// </summary>
/// <remarks>
/// <para>
/// Both shapes treat a completed channel as a clean end, and a channel reader checks its token before
/// it reads the channel's state, not after. A cancel that lands between the two, while a background
/// receive loop ends the channel cleanly in reaction to the same cancel, reaches the caller as a stream
/// that ended normally — or, when nothing had arrived yet, as an empty result. The check right after the
/// loop is where a cancel the reader did see would already have thrown, so it closes the window without
/// changing anything else.
/// </para>
/// <para>
/// The scanner looks through <c>.ConfigureAwait(…)</c> and <c>.WithCancellation(…)</c> around the
/// enumerated call and through whatever expression yields the reader (a field, <c>channel.Reader</c>, or
/// a hook that wraps it): only the trailing <c>ReadAllAsync</c> or <c>WaitToReadAsync</c> call matters.
/// The token is the argument of that call, or of <c>WithCancellation</c> when the call has none; a read
/// given no token at all is reported, since there is then nothing to re-check. Tokens are compared as
/// written, whitespace aside. <c>ReadAsync</c> and <c>TryRead</c> are out of scope: a completed channel
/// makes <c>ReadAsync</c> throw rather than report a clean end.
/// </para>
/// <para>
/// Syntactic, like the other guards here: a check placed after <c>Task.WhenAll</c>, before the loop, on
/// another token or in a helper is reported, because only the statement right after the loop is the
/// exit the won race already takes. Abstract subclasses are out of scope, as in
/// <see cref="SpeechClientDoorScanner"/>.
/// </para>
/// </remarks>
internal static class ChannelReadCancelScanner
{
    /// <summary>The shape name for an <c>await foreach</c> over <c>ReadAllAsync</c>.</summary>
    public const string ReadAllShape = "await foreach over ReadAllAsync";

    /// <summary>The shape name for a <c>while</c> loop on <c>await WaitToReadAsync</c>.</summary>
    public const string WaitToReadShape = "while (await WaitToReadAsync)";

    private static readonly HashSet<string> SpeechClientBases = new(StringComparer.Ordinal)
    {
        "SpeechSynthesizer",
        "SpeechRecognizer",
    };

    /// <summary>The channel reads in <paramref name="source"/>'s speech clients that are not re-checked.</summary>
    public static IReadOnlyList<ChannelReadCancelViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var violations = new List<ChannelReadCancelViolation>();
        foreach (var (client, loop, shape, token) in Reads(source))
        {
            if (token is null || !IsFollowedByCheck(loop, token))
            {
                violations.Add(new ChannelReadCancelViolation(
                    path, LineOf(loop), client.Identifier.Text, shape, token ?? string.Empty));
            }
        }

        return violations;
    }

    /// <summary>
    /// Every channel read the scanner recognises in <paramref name="source"/>'s speech clients, re-checked
    /// or not. The guard's liveness floor asserts on it: a scan that recognises no read passes having
    /// examined nothing.
    /// </summary>
    public static IReadOnlyList<ChannelReadSite> FindSites(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Reads(source)
            .Select(read => new ChannelReadSite(read.Client.Identifier.Text, LineOf(read.Loop), read.Shape))
            .ToList();
    }

    private static IEnumerable<(ClassDeclarationSyntax Client, StatementSyntax Loop, string Shape, string? Token)> Reads(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var client in SpeechClients(root))
        {
            foreach (var loop in client.DescendantNodes().OfType<StatementSyntax>())
            {
                switch (loop)
                {
                    case CommonForEachStatementSyntax forEach
                        when !forEach.AwaitKeyword.IsKind(SyntaxKind.None)
                            && ReadAllToken(forEach.Expression) is { IsRead: true } readAll:
                        yield return (client, loop, ReadAllShape, readAll.Token);
                        break;
                    case WhileStatementSyntax @while
                        when WaitToReadToken(@while.Condition) is { IsRead: true } waitToRead:
                        yield return (client, loop, WaitToReadShape, waitToRead.Token);
                        break;
                }
            }
        }
    }

    private static IEnumerable<ClassDeclarationSyntax> SpeechClients(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type => !type.Modifiers.Any(SyntaxKind.AbstractKeyword)
                && (type.BaseList?.Types.Any(baseType => SpeechClientBases.Contains(RightmostName(baseType.Type))) ?? false));

    /// <summary>
    /// Whether <paramref name="collection"/> is a <c>ReadAllAsync(…)</c> call, seen through
    /// <c>ConfigureAwait</c>, <c>WithCancellation</c> and parentheses, and the token it reads with.
    /// </summary>
    private static (bool IsRead, string? Token) ReadAllToken(ExpressionSyntax collection)
    {
        string? withCancellation = null;
        var current = collection;
        while (true)
        {
            if (current is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized.Expression;
                continue;
            }

            if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation)
            {
                switch (access.Name.Identifier.Text)
                {
                    case "ConfigureAwait":
                        current = access.Expression;
                        continue;
                    case "WithCancellation":
                        withCancellation ??= FirstArgument(invocation);
                        current = access.Expression;
                        continue;
                    case "ReadAllAsync":
                        return (true, FirstArgument(invocation) ?? withCancellation);
                }
            }

            return (false, null);
        }
    }

    /// <summary>
    /// Whether <paramref name="condition"/> awaits a <c>WaitToReadAsync(…)</c> call, alone or as part of
    /// a larger condition, and the token it waits with.
    /// </summary>
    private static (bool IsRead, string? Token) WaitToReadToken(ExpressionSyntax condition)
    {
        foreach (var awaitedExpression in condition.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Select(a => a.Expression))
        {
            var current = awaitedExpression;
            while (true)
            {
                if (current is ParenthesizedExpressionSyntax parenthesized)
                {
                    current = parenthesized.Expression;
                    continue;
                }

                if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } configure })
                {
                    current = configure.Expression;
                    continue;
                }

                break;
            }

            if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "WaitToReadAsync" } } wait)
                return (true, FirstArgument(wait));
        }

        return (false, null);
    }

    /// <summary>
    /// Whether the statement right after <paramref name="loop"/>, in the same block, is
    /// <c><paramref name="token"/>.ThrowIfCancellationRequested();</c>.
    /// </summary>
    private static bool IsFollowedByCheck(StatementSyntax loop, string token)
    {
        if (loop.Parent is not BlockSyntax block)
            return false;

        var index = block.Statements.IndexOf(loop);
        if (index < 0 || index + 1 >= block.Statements.Count)
            return false;

        return block.Statements[index + 1] is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ThrowIfCancellationRequested" } check,
                ArgumentList.Arguments.Count: 0,
            },
        } && Normalize(check.Expression) == token;
    }

    private static string? FirstArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Count == 0 ? null : Normalize(invocation.ArgumentList.Arguments[0].Expression);

    private static string Normalize(ExpressionSyntax expression) =>
        string.Concat(expression.ToString().Where(c => !char.IsWhiteSpace(c)));

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

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
