using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// A provider client type that ships without a row in the wire-conformance record.
/// <see cref="Path"/> is a display path (repo-relative when produced by the tree scan),
/// <see cref="Line"/> is 1-based and points at the class declaration.
/// </summary>
internal sealed record UnrecordedProviderViolation(string Path, int Line, string ClientType, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports provider client types the
/// wire-conformance record does not name.
/// </summary>
/// <remarks>
/// <para>
/// The record (<c>docs/guides/provider-wire-conformance.md</c>) is the artifact that makes
/// <i>not characterised</i> a visible state rather than a gap between rows. That only holds while
/// every shipping provider appears in it — a provider with no row is indistinguishable from a
/// working one until a user finds otherwise, which is precisely the failure this whole change was
/// opened to answer. So the record is checked against the code rather than maintained by memory.
/// </para>
/// <para>
/// A <b>provider client type</b> is a non-abstract class whose base list names
/// <c>SpeechSynthesizer</c> or <c>SpeechRecognizer</c>. Syntactic, because
/// <c>Verbara.Sdk.Governance.Tests</c> carries zero <c>ProjectReference</c>s by design and must never
/// gain one: a governance guard that compiles against the thing it governs can be broken by the same
/// edit it is supposed to catch.
/// </para>
/// <para>
/// One exclusion: types declared in <c>Verbara.Sdk.VoiceAi.Testing</c>. That package's charter is
/// in-memory doubles — they dial no endpoint, so they have no wire to conform to and no row to earn.
/// The exclusion is by PACKAGE, not by a <c>Fake</c> name prefix: a package boundary is a decision
/// somebody made, a naming convention is one somebody can drift away from silently.
/// </para>
/// <para>
/// The check is presence, never verdict. A row reading <c>not characterised</c> passes — that value
/// exists exactly so an unmeasured surface can be stated instead of omitted.
/// </para>
/// </remarks>
internal static class ConformanceRecordScanner
{
    /// <summary>Base types that make a class a provider client.</summary>
    private static readonly string[] ProviderBases = ["SpeechSynthesizer", "SpeechRecognizer"];

    /// <summary>The in-memory-doubles package, which has no wire and therefore no row.</summary>
    private const string TestingPackage = "Verbara.Sdk.VoiceAi.Testing";

    public static IReadOnlyList<UnrecordedProviderViolation> Scan(string source, string path, string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var violations = new List<UnrecordedProviderViolation>();
        foreach (var (clientType, line) in Declarations(source, path))
        {
            if (NamesClientType(record, clientType))
                continue;

            violations.Add(new UnrecordedProviderViolation(
                path,
                line,
                clientType,
                "provider client type has no row in the wire-conformance record — add one, even if it " +
                "reads 'not characterised'. A missing row is indistinguishable from a working provider."));
        }

        return violations;
    }

    /// <summary>
    /// The provider client types this source file declares. Public so the guard can check the
    /// record in BOTH directions: a row naming a type that no longer exists is a row nobody will
    /// ever be forced to update, and it reads as coverage of a provider that shipped away.
    /// </summary>
    public static IEnumerable<string> DeclaredClientTypes(string source, string path) =>
        Declarations(source, path).Select(d => d.ClientType);

    /// <summary>The types the record carries in its <b>Client type</b> column.</summary>
    public static IReadOnlyCollection<string> RecordedClientTypes(string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in ClientTypeCells(record))
        {
            if (cell.Length > 2 && cell[0] == '`' && cell[^1] == '`')
                types.Add(cell[1..^1]);
        }

        return types;
    }

    private static List<(string ClientType, int Line)> Declarations(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var declarations = new List<(string, int)>();
        if (path.Contains(TestingPackage, StringComparison.Ordinal))
            return declarations;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
                continue;
            if (!DerivesFromProviderBase(declaration))
                continue;

            var line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            declarations.Add((declaration.Identifier.Text, line));
        }

        return declarations;
    }

    private static bool DerivesFromProviderBase(ClassDeclarationSyntax declaration)
    {
        if (declaration.BaseList is null)
            return false;

        foreach (var baseType in declaration.BaseList.Types)
        {
            var name = baseType.Type switch
            {
                SimpleNameSyntax simple => simple.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                _ => null,
            };

            if (name is not null && Array.IndexOf(ProviderBases, name) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the record carries the type in the <b>Client type</b> COLUMN of a table row — the
    /// second cell of a Markdown row, written <c>`TypeName`</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a whole-file search, and that is not fastidiousness: measured against the
    /// record on 2026-08-18, six of the fourteen client types are already named in backticks in the
    /// narrative prose (<c>AssemblyAiSpeechRecognizer</c> three times, <c>LmntSpeechSynthesizer</c>
    /// three times). A file-wide <c>Contains</c> would therefore have let a provider pass on a
    /// passing mention in a paragraph about some other defect — a guard that accepts prose as a row
    /// is a guard that certifies exactly the omission it exists to catch.
    /// </remarks>
    private static bool NamesClientType(string record, string clientType)
    {
        var needle = $"`{clientType}`";
        foreach (var cell in ClientTypeCells(record))
        {
            if (cell.Equals(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The header cell that identifies the Client type column.</summary>
    private const string ClientTypeHeader = "Client type";

    /// <summary>
    /// The Client type cell of every Markdown table row in the record.
    /// "| Surface | Client type | … |" splits to ["", " Surface ", " Client type ", …, ""].
    /// </summary>
    /// <remarks>
    /// The column is located by its HEADER, not by its position, and a table whose header does not
    /// name it is skipped whole. This started as "the second cell of every row", which was true of
    /// a file containing only the two surface tables and stopped being true the moment the record
    /// grew a third: on 2026-08-19 a probe-results table landed whose second column reads
    /// <c>`101`, `transcript` then `done`</c> — a cell that begins and ends with a backtick, so the
    /// positional reader registered it as a client type and the reverse-direction guard failed
    /// naming it as an orphaned row. The guard was right to fail and its premise was wrong, which
    /// is the more useful half: a rule that depends on a file never gaining a table is a rule that
    /// breaks on the next honest edit, and it breaks with a message about the wrong thing.
    /// Skipping such tables is the same principle the prose exclusion already encodes: a cell in a
    /// table about something else is no more a row than a mention in a paragraph is. It is also
    /// strictly stricter — the positional reader would have accepted a provider named in the second
    /// column of any table in the file as a row it never was.
    /// </remarks>
    private static IEnumerable<string> ClientTypeCells(string record)
    {
        const int noTable = -1;
        var column = noTable;

        foreach (var line in record.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith('|'))
            {
                column = noTable;          // prose or a blank line ends the table
                continue;
            }

            var cells = trimmed.Split('|');
            if (cells.Length < 3)
                continue;

            if (column == noTable)
            {
                // The first row of a table is its header. A table that does not name the column is
                // a table about something else, and none of its cells are rows.
                column = Array.FindIndex(
                    cells, c => c.Trim().Equals(ClientTypeHeader, StringComparison.OrdinalIgnoreCase));
                if (column < 0)
                    column = int.MaxValue;   // read nothing further from this table
                continue;
            }

            if (column < cells.Length)
                yield return cells[column].Trim();
        }
    }
}
