using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Pure, I/O-free detector for the rule "no catch block in src is empty" (ADR-0053 R3, ADR-0050 E2):
/// a <c>catch</c> whose block holds neither a statement nor a comment. Reports each such clause as a
/// <see cref="DecisionGuardSite"/> keyed by the text of the whole <c>catch (…) when (…) { … }</c>
/// clause, so the guard can match it against <c>decision-guards-baseline.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// An empty catch makes a transport failure indistinguishable from a clean ending: the exception is
/// gone, and whatever runs next cannot tell a peer that hung up from a socket that broke. The two
/// ADRs this binds classify that failure in opposite directions — a PBX audio session reports it as
/// an ending (ADR-0053 R3), a speech client as a typed provider failure (ADR-0050 E2) — so the guard
/// takes no side. It asks only that the choice be written down: a statement that acts on the
/// exception, or a comment that says why it is ignored.
/// </para>
/// <para>
/// <b>Empty</b> means the block's braces enclose nothing but whitespace, line breaks, empty
/// statements (<c>;</c>) and blocks that are themselves empty. The last two are counted as empty
/// because they say nothing either: <c>catch { ; }</c> is a spelling of <c>catch { }</c>, not a
/// decision. A comment of any form inside the braces — <c>//</c>, <c>/* */</c>, even a doc comment
/// — is enough; so is a preprocessor directive or conditionally disabled code, because a syntactic
/// scan cannot know which configuration compiles the body and must not report a block that is
/// empty only under the symbols it happened to parse with.
/// </para>
/// <para>
/// The comment must be <b>inside</b> the braces, where the spec puts it
/// (<c>catch (IOException) { /* the peer left; the read loop reports it */ }</c>). A comment on the
/// line above the <c>catch</c>, or after its closing brace, belongs to the neighbouring code as far
/// as the syntax tree is concerned, and a reader skimming the block sees an empty one.
/// </para>
/// <para>
/// An exception filter does not excuse the block: <c>catch (IOException) when (stopping) { }</c>
/// swallows fewer exceptions, but swallows them just as silently. The filter is part of the key,
/// so two empty catches that differ only in their filter are two sites.
/// </para>
/// <para>
/// Detection is syntactic (real <see cref="CatchClauseSyntax"/> nodes), so a mention in a comment,
/// an XML doc or a string literal — this scanner's own fixtures included — can never produce a site.
/// </para>
/// </remarks>
internal static class EmptyCatchScanner
{
    /// <summary>The name this guard's entries carry in <c>decision-guards-baseline.json</c>.</summary>
    public const string GuardName = "empty-catch-block";

    /// <summary>
    /// The <c>catch</c> clauses in <paramref name="source"/> whose block holds neither a statement nor
    /// a comment. <see cref="DecisionGuardSite.Key"/> is the clause's text; the line is the
    /// <c>catch</c> keyword's.
    /// </summary>
    public static IReadOnlyList<DecisionGuardSite> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        return CatchClauses(source)
            .Where(clause => IsEmpty(clause.Block))
            .Select(clause => new DecisionGuardSite(
                path,
                clause.CatchKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                clause.ToString()))
            .ToList();
    }

    /// <summary>
    /// Counts every <c>catch</c> clause in <paramref name="source"/>, empty or not. The guard's
    /// liveness floor sums it over the tree: once the known sites are fixed the baseline is empty, and
    /// a detector that stopped finding catch clauses would then pass without inspecting one.
    /// </summary>
    public static int CountCatchClauses(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return CatchClauses(source).Count();
    }

    private static IEnumerable<CatchClauseSyntax> CatchClauses(string source) =>
        CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .OfType<CatchClauseSyntax>();

    /// <summary>
    /// Whether <paramref name="block"/> encloses nothing but whitespace, empty statements and empty
    /// nested blocks. Any other statement is content, and so is any trivia between the braces that is
    /// not whitespace or a line break (comments, directives, disabled text, skipped tokens).
    /// </summary>
    private static bool IsEmpty(BlockSyntax block)
    {
        if (block.DescendantNodes().Any(node => node is not (EmptyStatementSyntax or BlockSyntax)))
            return false;

        // Only the trivia strictly between the outer braces: the open brace's leading trivia and the
        // close brace's trailing trivia belong to the lines around the block, not to its body.
        var inner = TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.Span.Start);
        return block.DescendantTrivia()
            .Where(trivia => inner.Contains(trivia.Span))
            .All(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia));
    }
}
