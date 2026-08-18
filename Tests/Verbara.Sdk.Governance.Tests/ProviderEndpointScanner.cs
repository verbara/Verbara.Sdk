using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A single production provider endpoint written inline at a call site instead of being declared
/// once. <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record ProviderEndpointViolation(string Path, int Line, string Endpoint, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports production provider endpoints
/// that are written inside a method body rather than declared once — in an options type or a single
/// named constant.
/// </summary>
/// <remarks>
/// <para>
/// The motivating case was <c>LmntSpeechSynthesizer</c>'s HTTP route: an origin no configuration
/// could reach and no reader could audit without opening the file. A route that lives only at its
/// call site cannot be reviewed against a conformance record, cannot be pointed at a fake without a
/// bespoke test seam, and — the defect class this whole change exists for — can be wrong for as long
/// as nobody opens that file.
/// </para>
/// <para>
/// One structural rule, <b>EndpointInlinedAtCallSite</b>: a string literal or interpolated string
/// whose shape names a <c>http/https/ws/wss</c> scheme followed by a non-loopback host, appearing
/// anywhere other than a field or property <i>initializer</i>. A field initializer
/// (<c>private const string HttpOrigin = "…";</c>, <c>static readonly Uri Default = new("…");</c>)
/// and a property initializer (<c>public string BaseUri { get; set; } = "…";</c>) are the two
/// declared shapes this repo already uses, so both pass. A local variable, a <c>return</c>, an
/// argument, an expression-bodied member and a coalescing fallback are all call sites and all fail.
/// </para>
/// <para>
/// Two exclusions, both principled rather than convenient:
/// (a) <b>loopback hosts</b> (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>) are by construction not
///     production endpoints — they are test seams, and they are already governed by
///     <see cref="LoopbackSeamScanner"/>, which would double-report them;
/// (b) a <b>scheme with no host</b> (<c>"http://"</c> as a <c>Replace</c> argument, the shape
///     <c>AriClient</c> uses to derive its WebSocket origin) names no endpoint at all.
/// </para>
/// <para>
/// Detection is syntactic (real literal / interpolated-string nodes), so a URL inside an XML doc
/// <c>&lt;see href="…"/&gt;</c> or an ordinary comment can never produce a false positive — which is
/// what keeps this scanner's own test fixtures, and the vendor documentation links this repo cites
/// heavily, from self-flagging.
/// </para>
/// <para>
/// Exemptions are <b>inline markers</b>, following the <c>// fence-allow:</c> precedent set by
/// <see cref="SyncFenceScanner"/> rather than an external enumerated list. The marker sits at the
/// violating site, so a reader who opens the file sees the reason without opening a second one — the
/// same locality complaint that motivates the rule — and it is deleted by the same edit that removes
/// the site, which an external list is not. The category enum is CLOSED: a bare marker, an unknown
/// category or an empty reason is not a valid exemption.
/// </para>
/// </remarks>
internal static class ProviderEndpointScanner
{
    /// <summary>
    /// Valid allow-marker: literal <c>// endpoint-allow:</c>, a category from the CLOSED enum, an
    /// em-dash (<c>—</c>) or <c>--</c> separator, then at least one non-space char (the reason).
    /// </summary>
    private static readonly Regex AllowMarker = new(
        @"//\s*endpoint-allow:\s*(REGION-TEMPLATED|PENDING-VERIFICATION)\s*(?:—|--)\s*\S",
        RegexOptions.None);

    /// <summary>Shape of an endpoint: one of these, then at least one host character.</summary>
    private static readonly string[] Schemes = ["http://", "https://", "ws://", "wss://"];

    /// <summary>
    /// Hosts that are never a production endpoint. <c>localhost</c> is here because a fake-server
    /// seam is not this scanner's business — <see cref="LoopbackSeamScanner"/> owns that host token
    /// and reports it with the right diagnosis.
    /// </summary>
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>Sites that name a production endpoint at a call site and carry no valid marker.</summary>
    public static IReadOnlyList<ProviderEndpointViolation> Scan(string source, string path) =>
        Detect(source, path, excused: false);

    /// <summary>
    /// The complement of <see cref="Scan"/>: sites that WOULD be violations but are excused by a
    /// valid marker. This is what the exemption ratchet counts — a marker with no site behind it
    /// counts for nothing, so the tally cannot be padded.
    /// </summary>
    public static IReadOnlyList<ProviderEndpointViolation> ScanExempted(string source, string path) =>
        Detect(source, path, excused: true);

    private static List<ProviderEndpointViolation> Detect(string source, string path, bool excused)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var violations = new List<ProviderEndpointViolation>();

        // Valid allow-markers are REAL single-line comment trivia (never string/XML-doc text).
        // 0-based start lines. `commentLines` records every single-line comment so the marker can be
        // found anywhere in the contiguous comment block directly above the site: an endpoint
        // exemption needs a sentence or three of reasoning, and forcing it onto one physical line
        // would buy syntactic tidiness by making the reason worse.
        var markerLines = new HashSet<int>();
        var commentLines = new HashSet<int>();
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
                continue;

            var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line;
            commentLines.Add(line);
            if (AllowMarker.IsMatch(trivia.ToString()))
                markerLines.Add(line);
        }

        foreach (var node in root.DescendantNodes())
        {
            var shape = node switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                    => literal.Token.ValueText,
                InterpolatedStringExpressionSyntax interpolated => BuildShape(interpolated),
                _ => null,
            };

            if (shape is null || !NamesProductionEndpoint(shape))
                continue;

            if (IsDeclaredOnce(node))
                continue;

            if (IsAllowed(node, markerLines, commentLines) != excused)
                continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new ProviderEndpointViolation(
                path,
                line,
                shape,
                "production endpoint written at a call site — declare it once, in the provider's " +
                "options type or a single named constant, so configuration can reach it and a " +
                "reviewer can audit it without opening this file."));
        }

        return violations;
    }

    /// <summary>
    /// True when the reconstructed text names a known scheme immediately followed by something that
    /// can actually be a host, and that host is not loopback. Every occurrence of every scheme is
    /// examined, not just the first — prose that quotes two schemes ("must start with wss:// or
    /// ws://.") must be rejected on both.
    /// </summary>
    private static bool NamesProductionEndpoint(string shape)
    {
        foreach (var scheme in Schemes)
        {
            var from = 0;
            while (true)
            {
                var index = shape.IndexOf(scheme, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                from = index + scheme.Length;
                var host = shape[from..];

                if (StartsHost(host) && !IsLoopback(host))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the text directly after the scheme separator can begin a host: a letter, a digit,
    /// an interpolation hole (a templated origin such as
    /// <c>$"https://{region}.tts.speech.microsoft.com"</c>) or an IPv6 bracket.
    /// </summary>
    /// <remarks>
    /// This is what separates an endpoint from PROSE THAT QUOTES ONE, and it is not a hypothetical
    /// distinction — it was measured. The first run of this scanner over <c>src/</c> reported ten
    /// sites, of which eight were the <c>ErrorMessage</c> of a <c>[RegularExpression]</c> attribute
    /// ("BaseUri must start with wss:// or ws://."). Those are validation prose sitting in attribute
    /// metadata, not routes any client dials. A bare scheme token, and a scheme followed by a space
    /// or a dot, name no host and therefore no endpoint.
    /// </remarks>
    private static bool StartsHost(string host) =>
        host.Length > 0 && (char.IsLetterOrDigit(host[0]) || host[0] is '{' or '[');

    private static bool IsLoopback(string host)
    {
        foreach (var loopback in LoopbackHosts)
        {
            if (host.StartsWith(loopback, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the node sits inside a FIELD or PROPERTY initializer — the two shapes this repo
    /// already uses to declare an endpoint once. A local declaration also carries an
    /// <see cref="EqualsValueClauseSyntax"/>, which is why the walk insists on reaching a field or
    /// property declaration rather than merely finding an initializer.
    /// </summary>
    private static bool IsDeclaredOnce(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }:
                    return true;
                case VariableDeclaratorSyntax when current.FirstAncestorOrSelf<FieldDeclarationSyntax>() is not null:
                    return true;

                // Anything with a body is a call site; stop before the walk escapes into the
                // enclosing type and mistakes a method-local literal for a declaration.
                case BaseMethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case ArrowExpressionClauseSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a valid marker sits on the site's own line span, or anywhere inside the unbroken
    /// run of single-line comments directly above it. The run stops at the first line that is not a
    /// comment, so a marker cannot reach across intervening code and excuse a site it does not
    /// belong to.
    /// </summary>
    private static bool IsAllowed(SyntaxNode node, HashSet<int> markerLines, HashSet<int> commentLines)
    {
        var span = node.GetLocation().GetLineSpan();
        for (var line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
        {
            if (markerLines.Contains(line))
                return true;
        }

        for (var line = span.StartLinePosition.Line - 1; line >= 0 && commentLines.Contains(line); line--)
        {
            if (markerLines.Contains(line))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reconstructs the interpolated string's SHAPE: literal text verbatim, every interpolation
    /// hole collapsed to <c>{}</c>. An origin templated from an option
    /// (<c>$"https://{region}.tts.speech.microsoft.com"</c>) still names a scheme and a host, so it
    /// is still an endpoint — the hole sits in the host, not in place of it.
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
}
