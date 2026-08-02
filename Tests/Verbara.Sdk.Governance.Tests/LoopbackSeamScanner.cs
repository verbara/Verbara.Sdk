using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A single fake-server seam that names the loopback host as <c>localhost</c> instead of the
/// IPv4 literal <c>127.0.0.1</c>. <see cref="Path"/> is a display path (repo-relative when produced
/// by the tree scan), <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record LoopbackSeamViolation(string Path, int Line, string Rule, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports fake-server seams that dial or
/// bind <c>localhost</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>localhost</c> resolves <c>::1</c> BEFORE <c>127.0.0.1</c>, and the in-process fakes bind
/// IPv4-only listeners (<c>new TcpListener(IPAddress.Loopback, 0)</c>) — so a second server can
/// bind the SAME port number on <c>::1</c> without <c>EADDRINUSE</c>, and a client dialling
/// <c>localhost</c> silently reaches the wrong one (observed as a WebSocket handshake answered
/// <c>200</c> instead of <c>101</c>). IPv4 literals on both the bind and the dial side make the
/// second bind fail loudly into the fakes' existing port-retry loops.
/// </para>
/// <para>
/// Two structural rules, no ignore list:
/// (a) <b>LoopbackInterpolation</b> — a <c>://localhost:</c> whose port is an INTERPOLATION HOLE
///     (<c>$"ws://localhost:{port}/…"</c>). A dynamic port is by construction a test fake; every
///     legitimate product default names a fixed literal port (ARI 8088, OTLP 4317, Toxiproxy 8474)
///     and therefore never matches.
/// (b) <b>HttpListenerPrefix</b> — any <c>…Prefixes.Add(…)</c> argument naming <c>://localhost:</c>,
///     literal port or not, since an <see cref="System.Net.HttpListener"/> prefix is always a bind.
/// </para>
/// <para>
/// Detection is syntactic (real interpolated-string / invocation nodes), so comment, XML-doc, and
/// plain string-literal mentions can never produce a false positive — which is also what keeps this
/// scanner's own test fixtures (C# snippets embedded as ordinary string literals) from self-flagging.
/// </para>
/// </remarks>
internal static class LoopbackSeamScanner
{
    /// <summary>Shape of an interpolated <c>localhost</c> port: scheme separator, host, hole.</summary>
    private const string InterpolatedLoopbackShape = "://localhost:{}";

    /// <summary>Raw needle for prefix-registration arguments (literal or interpolated).</summary>
    private const string LoopbackHostToken = "://localhost:";

    public static IReadOnlyList<LoopbackSeamViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var violations = new List<LoopbackSeamViolation>();

        // (a) Interpolated strings whose loopback port is a hole => a fake-server port.
        foreach (var interpolated in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            if (!BuildShape(interpolated).Contains(InterpolatedLoopbackShape, StringComparison.OrdinalIgnoreCase))
                continue;

            var line = interpolated.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new LoopbackSeamViolation(
                path,
                line,
                "LoopbackInterpolation",
                "Fake-server seam dials 'localhost' with an interpolated port — 'localhost' resolves ::1 first, which the IPv4-only test listener does not own. Use the literal '127.0.0.1'."));
        }

        // (b) HttpListener prefix registrations: '<x>.Prefixes.Add(<arg naming localhost>)'.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsPrefixesAdd(invocation.Expression))
                continue;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (!argument.Expression.ToString().Contains(LoopbackHostToken, StringComparison.OrdinalIgnoreCase))
                    continue;

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                violations.Add(new LoopbackSeamViolation(
                    path,
                    line,
                    "HttpListenerPrefix",
                    "HttpListener prefix binds 'localhost' — it would claim a port number the IPv4-only fakes already hold on 127.0.0.1, cross-wiring two servers instead of raising EADDRINUSE. Use the literal '127.0.0.1'."));
                break;
            }
        }

        return violations;
    }

    /// <summary>
    /// Reconstructs the interpolated string's SHAPE: literal text kept verbatim, every
    /// interpolation hole collapsed to <c>{}</c>. This is what lets the port form (hole) be told
    /// apart from a fixed product default (literal digits).
    /// </summary>
    private static string BuildShape(InterpolatedStringExpressionSyntax node)
    {
        var sb = new StringBuilder();
        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax text)
                sb.Append(text.TextToken.ValueText);
            else
                sb.Append("{}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// True for an invocation target of the form <c>&lt;receiver&gt;.Prefixes.Add</c> — matches
    /// <c>listener.Prefixes.Add(...)</c> and <c>_listener.Prefixes.Add(...)</c> alike.
    /// </summary>
    private static bool IsPrefixesAdd(ExpressionSyntax expression)
    {
        return expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } add
            && add.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Prefixes" };
    }
}
