using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A type that defines the AudioSocket frame format in a file that does not cite the protocol's
/// source. <see cref="Path"/> is a display path, <see cref="Line"/> is 1-based and points at the type's
/// name.
/// </summary>
internal sealed record AudioSocketCitationViolation(string Path, int Line, string Type);

/// <summary>
/// Pure, I/O-free detector for ADR-0060 R1: a source file that defines the AudioSocket frame format
/// cites Asterisk's <c>res_audiosocket.h</c> in a comment.
/// </summary>
/// <remarks>
/// <para>
/// A file <b>defines the format</b> when it declares either of the two things each implementation
/// copies from Asterisk: an enum carrying the frame kinds <c>Hangup</c>, <c>Uuid</c> and <c>Audio</c>
/// (Asterisk's <c>enum ast_audiosocket_msg_kind</c>), or a type whose name contains
/// <c>AudioSocket</c> and that declares a <c>HeaderSize</c> constant (the header layout). Both
/// implementations have one of each, which is the four files ADR-0060 lists; a third implementation
/// would have them too, and is held to the rule without anyone adding it to a list.
/// </para>
/// <para>
/// <b>Cites</b> means <c>res_audiosocket.h</c> appears in a comment of the file: a line comment, a block
/// comment or an XML doc comment. A string is data the code carries, not a pointer a reader follows,
/// so a string alone does not count. Neither does <c>res_audiosocket.so</c>: the loadable module is
/// what a capture ran against, not the definition R1 names.
/// </para>
/// </remarks>
internal static class AudioSocketCitationScanner
{
    /// <summary>The citation ADR-0060 R1 requires, as the format files write it.</summary>
    public const string Citation = "res_audiosocket.h";

    /// <summary>The frame kinds an enum must declare to be a copy of Asterisk's kind table.</summary>
    private static readonly string[] FrameKinds = ["Hangup", "Uuid", "Audio"];

    private const string HeaderSizeConstant = "HeaderSize";

    private const string AudioSocketTypeMarker = "AudioSocket";

    /// <summary>
    /// One violation per format-defining type in <paramref name="source"/>, when the file does not cite
    /// <see cref="Citation"/> in a comment.
    /// </summary>
    public static IReadOnlyList<AudioSocketCitationViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (CitesTheHeader(root))
            return [];

        return FormatTypes(root)
            .Select(type => new AudioSocketCitationViolation(
                path,
                type.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                type.Identifier.Text))
            .ToList();
    }

    /// <summary>
    /// The names of the types in <paramref name="source"/> that define the AudioSocket format, cited or
    /// not. The guard's liveness test asserts on it: a detector that stopped recognising the format
    /// files would otherwise pass having judged none of them.
    /// </summary>
    public static IReadOnlyList<string> FindFormatDefinitions(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return FormatTypes(root).Select(type => type.Identifier.Text).ToList();
    }

    private static IEnumerable<BaseTypeDeclarationSyntax> FormatTypes(SyntaxNode root) =>
        root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(DefinesTheFormat);

    private static bool DefinesTheFormat(BaseTypeDeclarationSyntax type) => type switch
    {
        EnumDeclarationSyntax kinds =>
            FrameKinds.All(kind => kinds.Members.Any(member => member.Identifier.Text == kind)),
        TypeDeclarationSyntax codec when codec.Identifier.Text.Contains(AudioSocketTypeMarker, StringComparison.Ordinal) =>
            codec.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword))
                .SelectMany(field => field.Declaration.Variables)
                .Any(variable => variable.Identifier.Text == HeaderSizeConstant),
        _ => false,
    };

    private static bool CitesTheHeader(SyntaxNode root) =>
        root.DescendantTrivia(descendIntoTrivia: false)
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Any(trivia => trivia.ToFullString().Contains(Citation, StringComparison.Ordinal));
}
