using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A WebSocket speech client that does not produce one of the typed provider failures.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based and points at the client's class name.
/// </summary>
internal sealed record SpeechClientDoorViolation(string Path, int Line, string Client, string Door);

/// <summary>
/// Pure, I/O-free detector for the doors rule (ADR-0049 D1, ADR-0050 E2 and E7): every non-abstract
/// <c>SpeechSynthesizer</c> or <c>SpeechRecognizer</c> subclass declared in a file that references
/// <c>ClientWebSocket</c> calls each of <c>SpeechProviderFailureException.FromErrorFrame</c>,
/// <c>FromCloseStatus</c>, <c>FromTransport</c> and <c>FromHandshake</c>. Reports one violation per
/// missing door per client.
/// </summary>
/// <remarks>
/// <para>
/// Before ADR-0050, a vendor's refusal left every WebSocket client through one of three doors and
/// reached the caller as a stream that ended empty and successful: an error frame that fell into a
/// discard branch, a close code nobody read, a <c>WebSocketException</c> caught and turned into a
/// <c>break</c>. A handshake rejection escaped raw, typed by whichever validation regime the vendor
/// happened to use. Each of the four factories is the one place a client turns one of those into the
/// typed failure, so a client that no longer calls one has reopened its door.
/// </para>
/// <para>
/// <b>Presence, not correctness.</b> The scan asks only whether each factory is called somewhere in
/// the client's declaration, lambdas and local functions included. A call on the wrong branch passes;
/// the provider's own tests own that. What this catches is the regression that shipped before: a door
/// removed without anything noticing.
/// </para>
/// <para>
/// A call counts only on <c>SpeechProviderFailureException</c> itself, qualified or not, or bare when
/// the file imports that type with <c>using static</c>. A same-named helper of the client's own, or a
/// factory on another type, is not the typed failure ADR-0050 E1 requires and does not close the door.
/// </para>
/// <para>
/// A file "references <c>ClientWebSocket</c>" when that name appears as a real identifier: a type, a
/// construction or a qualified name. A mention in a comment, an XML doc or a string does not bring a
/// file into scope, and a door named only in a comment or a string is still missing. Abstract
/// subclasses are out of scope: they own no session, and the concrete client that inherits them is
/// the one a caller gets. Known blind spot: a client split into partial declarations across files is
/// judged per file, so a door called in another part is reported missing (a false red, never a false
/// green); no client in the two packages is partial.
/// </para>
/// </remarks>
internal static class SpeechClientDoorScanner
{
    /// <summary>The four factories, one per way a failure can leave a WebSocket session.</summary>
    public static readonly IReadOnlyList<string> Doors =
        ["FromErrorFrame", "FromCloseStatus", "FromTransport", "FromHandshake"];

    private static readonly HashSet<string> SpeechClientBases = new(StringComparer.Ordinal)
    {
        "SpeechSynthesizer",
        "SpeechRecognizer",
    };

    private const string FailureType = "SpeechProviderFailureException";

    private const string WebSocketType = "ClientWebSocket";

    /// <summary>
    /// The doors each WebSocket client in <paramref name="source"/> does not call, in the order of
    /// <see cref="Doors"/>. A file that does not reference <c>ClientWebSocket</c> reports nothing.
    /// </summary>
    public static IReadOnlyList<SpeechClientDoorViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var importsFailureType = ImportsFailureTypeStatically(root);

        var violations = new List<SpeechClientDoorViolation>();
        foreach (var client in WebSocketClients(root))
        {
            var called = CalledDoors(client, importsFailureType);
            var line = client.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.AddRange(Doors
                .Where(door => !called.Contains(door))
                .Select(door => new SpeechClientDoorViolation(path, line, client.Identifier.Text, door)));
        }

        return violations;
    }

    /// <summary>
    /// The names of the WebSocket clients in <paramref name="source"/>, the population
    /// <see cref="Scan"/> judges. The guard's liveness floor asserts on it: a scan that walks every
    /// file but recognises no client passes having examined nothing.
    /// </summary>
    public static IReadOnlyList<string> FindWebSocketClients(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return WebSocketClients(root).Select(client => client.Identifier.Text).ToList();
    }

    private static IEnumerable<ClassDeclarationSyntax> WebSocketClients(SyntaxNode root)
    {
        var referencesWebSocket = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => identifier.Identifier.Text == WebSocketType);
        if (!referencesWebSocket)
            return [];

        return root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type => !type.Modifiers.Any(SyntaxKind.AbstractKeyword) && DerivesFromSpeechClient(type));
    }

    private static bool DerivesFromSpeechClient(ClassDeclarationSyntax type) =>
        type.BaseList?.Types.Any(baseType => SpeechClientBases.Contains(RightmostName(baseType.Type))) ?? false;

    /// <summary>
    /// The doors called inside <paramref name="client"/>: <c>SpeechProviderFailureException.FromX(…)</c>
    /// under any qualification, or a bare <c>FromX(…)</c> when the file imports the type statically.
    /// </summary>
    private static HashSet<string> CalledDoors(ClassDeclarationSyntax client, bool importsFailureType)
    {
        return client.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax access when ReceiverName(access.Expression) == FailureType =>
                    access.Name.Identifier.Text,
                IdentifierNameSyntax bare when importsFailureType => bare.Identifier.Text,
                _ => null,
            })
            .OfType<string>()
            .Where(door => Doors.Contains(door))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ImportsFailureTypeStatically(SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Any(directive => !directive.StaticKeyword.IsKind(SyntaxKind.None)
                && directive.NamespaceOrType is { } name
                && RightmostName(name) == FailureType);

    /// <summary>
    /// <c>SpeechProviderFailureException</c> and <c>Verbara.Sdk.VoiceAi.SpeechProviderFailureException</c>
    /// (a member-access chain in expression position) both reduce to the type's simple name.
    /// </summary>
    private static string? ReceiverName(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
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
