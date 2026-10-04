using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A test source line that names <c>TaskContinuationOptions.ExecuteSynchronously</c>.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record SynchronousContinuationSite(string Path, int Line, string Text);

/// <summary>
/// Pure, I/O-free detector for "a test never reads state through a synchronous continuation": it
/// reports every use of <c>TaskContinuationOptions.ExecuteSynchronously</c> in a C# source.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteSynchronously</c> is a request, not a promise. The runtime runs such a continuation
/// inside the call that completes its antecedent only when it agrees to: it declines when the
/// antecedent runs its continuations asynchronously (a <c>TaskCompletionSource</c> built with
/// <c>RunContinuationsAsynchronously</c>, a channel's completion without
/// <c>AllowSynchronousContinuations</c>), when the completion happens at a stack too deep to inline
/// on, and when the antecedent had already completed before the continuation was attached. A test
/// that reads a state from such a continuation, believing it reads "the state at the moment the
/// antecedent completed", then reads it later, on whichever thread runs it: over a flag that only
/// goes false→true that turns the defect the test names into a green, and over a state that moves on
/// it fails on correct code. Which case a test is in depends on code in <c>src/</c> the test does not
/// own, so the guard bans the option from the test tree outright. A test reads such a state inside
/// the call that makes the moment (its handler, its fake, its channel writer), or acts there.
/// </para>
/// <para>
/// Detection keys on every identifier node whose text is <c>ExecuteSynchronously</c>: the enum
/// member is the only symbol of that name among the BCL's task types. Keying on the identifier
/// rather than on the member-access shape makes one rule of every spelling — qualified,
/// fully qualified, through a <c>using</c> alias, bare under <c>using static</c> — alone or in a
/// <c>|</c> with other flags. One spelling is out of reach and is not reported: a numeric cast
/// such as <c>(TaskContinuationOptions)0x80000</c>.
/// </para>
/// <para>
/// Detection is syntactic (real identifier nodes), so a mention in a comment, an XML doc or a string
/// literal — this scanner's own fixtures included — can never produce a site. Production code under
/// <c>src/</c> is not this rule's concern: a continuation there that only removes a finished task
/// from a set, observes a fault or logs an ending reads nothing whose value depends on when it runs.
/// </para>
/// </remarks>
internal static class SynchronousContinuationScanner
{
    /// <summary>The enum member's name. Written as a string so this file never names the member itself.</summary>
    private const string MemberName = "ExecuteSynchronously";

    /// <summary>
    /// Every identifier node in <paramref name="source"/> whose text is the option's name, one site per
    /// node, with the text of its source line trimmed.
    /// </summary>
    public static IReadOnlyList<SynchronousContinuationSite> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var text = tree.GetText();

        return tree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == MemberName)
            .Select(identifier =>
            {
                var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line;
                return new SynchronousContinuationSite(path, line + 1, text.Lines[line].ToString().Trim());
            })
            .ToList();
    }

    /// <summary>The guard's failure message: one line per site, and what to write instead.</summary>
    public static string BuildFailureMessage(IReadOnlyList<SynchronousContinuationSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var lines = sites.Select(site => $"  {site.Path}:{site.Line}: {site.Text}");
        return $"{sites.Count} test source line(s) request a synchronous continuation, which the runtime " +
            "may decline (an antecedent that runs its continuations asynchronously, a deep stack, an " +
            "antecedent already complete). Read or act inside the call that makes the moment — the " +
            "handler, the fake, the channel writer the test owns — instead:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines);
    }
}
