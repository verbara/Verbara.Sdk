using System.Globalization;
using System.Text.RegularExpressions;
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
/// A row of the wire-conformance record that names a client type but does not state what its verdict
/// rests on (ADR-0048 D8) or, for a WebSocket surface, where the vendor refuses a bad credential
/// (ADR-0049 D3). <see cref="Line"/> is 1-based within the record; <see cref="ClientType"/> is the
/// row's Client type cell with its code span removed.
/// </summary>
internal sealed record ConformanceRowViolation(int Line, string ClientType, string Detail);

/// <summary>
/// What <see cref="ConformanceRecordScanner.CheckRows"/> found, and how much it read: the counts are
/// the guard's liveness evidence, because a reader that finds no rows passes every row.
/// </summary>
internal sealed record ConformanceRowCheck(
    IReadOnlyList<ConformanceRowViolation> Violations,
    int ClientTypeRows,
    int WebSocketRows);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports provider client types the
/// wire-conformance record does not name, and reads the record's own tables to report rows that do
/// not state what they rest on (<see cref="CheckRows"/>).
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
/// <para>
/// A row, once present, must say what it rests on: an evidence class from the record's declared
/// vocabulary and a date (ADR-0048 D8), and for a <c>wss://</c> surface a measured validation point
/// (ADR-0049 D3). That half reads only the record, never source, and both vocabularies it applies are
/// read from the record too.
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

        return ClientTypeCells(record)
            .Where(cell => cell.Length > 2 && cell[0] == '`' && cell[^1] == '`')
            .Select(cell => cell[1..^1])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The evidence classes the record declares: the code spans of the <b>Class</b> column of its
    /// "How to read a row" table (<c>live + both controls</c>, …, <c>not characterised</c>).
    /// </summary>
    /// <remarks>
    /// Read from the record rather than copied into this guard, so the vocabulary has one home. A
    /// class the record adds is admitted the day it is declared; a class it withdraws turns every row
    /// still claiming it red.
    /// </remarks>
    public static IReadOnlyCollection<string> DeclaredEvidenceClasses(string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return TableRows(record)
            .Select(row => row.Cell(EvidenceClassHeader))
            .OfType<string>()
            .Where(IsCodeSpan)
            .Select(cell => cell[1..^1])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The validation points the record declares: the code span that opens each bullet of the list
    /// under its <c>**Validation point**</c> definition (<c>handshake</c>, <c>in-band</c>).
    /// </summary>
    /// <remarks>
    /// The list is the first run of <c>- `…`</c> bullets after the definition, and it ends at the first
    /// line that is neither a bullet, an indented continuation nor blank. A later bullet list in the
    /// record is about something else and adds nothing.
    /// </remarks>
    public static IReadOnlyCollection<string> DeclaredValidationPoints(string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var points = new HashSet<string>(StringComparer.Ordinal);
        var inDefinition = false;
        var inList = false;
        foreach (var line in Lines(record))
        {
            if (!inDefinition)
            {
                inDefinition = line.StartsWith(ValidationPointDefinition, StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("- `", StringComparison.Ordinal))
            {
                inList = true;
                var end = line.IndexOf('`', 3);
                if (end > 3)
                    points.Add(line[3..end]);
                continue;
            }

            if (!inList || line.Length == 0 || line.StartsWith(' '))
                continue;   // the definition's own paragraph, a blank line, or a bullet's continuation

            break;          // the next paragraph ends the list
        }

        return points;
    }

    /// <summary>
    /// Checks every row of every table that names a <b>Client type</b> column: its <b>Evidence</b>
    /// cell is one of <see cref="DeclaredEvidenceClasses"/> and its <b>Date</b> cell carries an ISO
    /// date (ADR-0048 D8); and when its <b>Transport</b> cell is a <c>wss://</c> URI, its
    /// <b>Validation point</b> cell names one of <see cref="DeclaredValidationPoints"/> as a code span
    /// (ADR-0049 D3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This checks that a row <i>says</i> what it rests on, never that what it says is true: no guard
    /// can tell a live probe from a row that claims one. What it removes is the state D8 exists to
    /// forbid — a verdict with nothing under it — and the state D3 withdrew: a WebSocket surface whose
    /// credential check was inferred, or never looked for, reading like one that was measured.
    /// </para>
    /// <para>
    /// A missing column is a missing cell, so dropping the Evidence or Date column from a table
    /// reports every row in it, and a <c>wss://</c> row in a table with no Validation point column is
    /// reported too. A row whose Transport cell cannot be read is reported as well: the WebSocket rule
    /// keys on that cell, and a table without it would otherwise exempt every WebSocket surface in it.
    /// </para>
    /// <para>
    /// The validation point must be a code span because the cell is free text — Cartesia STT's reads
    /// <c>`handshake` (credential) + `in-band` (session)</c> — so a bare word would admit prose such as
    /// "no handshake control". The evidence cell is compared whole, so its code span is optional.
    /// </para>
    /// </remarks>
    public static ConformanceRowCheck CheckRows(string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var evidenceClasses = DeclaredEvidenceClasses(record);
        var validationPoints = DeclaredValidationPoints(record);
        var violations = new List<ConformanceRowViolation>();
        var clientTypeRows = 0;
        var webSocketRows = 0;

        foreach (var row in TableRows(record).Where(row => row.Cell(ClientTypeHeader) is not null))
        {
            var clientTypeCell = row.Cell(ClientTypeHeader)!;
            clientTypeRows++;
            var clientType = IsCodeSpan(clientTypeCell) ? clientTypeCell[1..^1] : clientTypeCell;
            void Report(string detail) => violations.Add(new ConformanceRowViolation(row.Line, clientType, detail));

            switch (row.Cell(EvidenceHeader))
            {
                case null:
                    Report("no evidence cell: the table has no 'Evidence' column, so the row states no evidence class");
                    break;
                case var evidence when evidence.Length == 0:
                    Report("empty evidence cell: a row states its evidence class, even when it is 'not characterised'");
                    break;
                case var evidence when !evidenceClasses.Contains(IsCodeSpan(evidence) ? evidence[1..^1] : evidence):
                    Report($"evidence '{evidence}' is not a class the record declares " +
                        $"({Quoted(evidenceClasses)})");
                    break;
            }

            switch (row.Cell(DateHeader))
            {
                case null:
                    Report("no date cell: the table has no 'Date' column, so the row states no date");
                    break;
                case var date when !CarriesIsoDate(date):
                    Report($"date cell '{date}' carries no yyyy-MM-dd date");
                    break;
            }

            switch (row.Cell(TransportHeader))
            {
                case null:
                    Report("no transport cell: the table has no 'Transport' column, so whether the row is a " +
                        "WebSocket surface, and owes a validation point, cannot be read");
                    break;
                case var transport when transport.Contains(WebSocketScheme, StringComparison.OrdinalIgnoreCase):
                    webSocketRows++;
                    var point = row.Cell(ValidationPointHeader);
                    if (point is null || !validationPoints.Any(p => point.Contains($"`{p}`", StringComparison.Ordinal)))
                    {
                        Report($"WebSocket row's validation point '{point ?? "(no column)"}' names none of the " +
                            $"measured points the record declares ({Quoted(validationPoints)})");
                    }

                    break;
            }
        }

        return new ConformanceRowCheck(violations, clientTypeRows, webSocketRows);
    }

    private static List<(string ClientType, int Line)> Declarations(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Contains(TestingPackage, StringComparison.Ordinal))
            return [];

        return CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(declaration => !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))
                && DerivesFromProviderBase(declaration))
            .Select(declaration => (
                ClientType: declaration.Identifier.Text,
                Line: declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
            .ToList();
    }

    private static bool DerivesFromProviderBase(ClassDeclarationSyntax declaration) =>
        declaration.BaseList is { } baseList
        && baseList.Types.Any(baseType => BaseTypeName(baseType.Type) is { } name && Array.IndexOf(ProviderBases, name) >= 0);

    /// <summary>
    /// The simple name a base-list entry ends in — <c>SpeechRecognizer</c> for both
    /// <c>SpeechRecognizer</c> and <c>Verbara.Sdk.VoiceAi.SpeechRecognizer</c> — or
    /// <see langword="null"/> for any other shape.
    /// </summary>
    private static string? BaseTypeName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        _ => null,
    };

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

    /// <summary>The header cells the row check reads, located by name like the Client type column.</summary>
    private const string EvidenceHeader = "Evidence";
    private const string DateHeader = "Date";
    private const string TransportHeader = "Transport";
    private const string ValidationPointHeader = "Validation point";

    /// <summary>The header of the table that declares the evidence vocabulary.</summary>
    private const string EvidenceClassHeader = "Class";

    /// <summary>The paragraph whose bullet list declares the validation points.</summary>
    private const string ValidationPointDefinition = "**Validation point**";

    private const string WebSocketScheme = "wss://";

    /// <summary>An ISO date not glued to further digits; <see cref="CarriesIsoDate"/> also parses it.</summary>
    private static readonly Regex IsoDate = new(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)", RegexOptions.CultureInvariant);

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
    private static IEnumerable<string> ClientTypeCells(string record) =>
        TableRows(record).Select(row => row.Cell(ClientTypeHeader)).OfType<string>();

    /// <summary>
    /// One body row of a Markdown table: its 1-based line in the record, and the table's header cells
    /// and the row's own cells, both trimmed and both indexed as the split leaves them (cell 0 is the
    /// empty text before the leading pipe).
    /// </summary>
    private sealed record TableRow(int Line, string[] Header, string[] Cells)
    {
        /// <summary>The row's cell under <paramref name="header"/>, or null when the table has no such column or the row is short.</summary>
        public string? Cell(string header)
        {
            var column = Array.FindIndex(Header, h => h.Equals(header, StringComparison.OrdinalIgnoreCase));
            return column >= 0 && column < Cells.Length ? Cells[column] : null;
        }
    }

    /// <summary>
    /// Every body row of every Markdown table in the record. A table starts at the first line opening
    /// with a pipe, which is its header; the delimiter row under it is skipped; prose or a blank line
    /// ends it. A line of fewer than three cells is not a row.
    /// </summary>
    private static IEnumerable<TableRow> TableRows(string record)
    {
        string[]? header = null;
        var lineNumber = 0;

        foreach (var line in Lines(record))
        {
            lineNumber++;
            if (!line.StartsWith('|'))
            {
                header = null;          // prose or a blank line ends the table
                continue;
            }

            var cells = line.Split('|');
            if (cells.Length < 3)
                continue;

            var trimmed = Array.ConvertAll(cells, c => c.Trim());
            if (header is null)
            {
                header = trimmed;       // the first row of a table is its header
                continue;
            }

            if (IsDelimiterRow(trimmed))
                continue;

            yield return new TableRow(lineNumber, header, trimmed);
        }
    }

    private static IEnumerable<string> Lines(string record) =>
        record.Split('\n').Select(line => line.TrimEnd('\r'));

    /// <summary>"|---|:---:|" — every cell empty or dashes with optional alignment colons, at least one of them dashes.</summary>
    private static bool IsDelimiterRow(string[] cells) =>
        cells.Any(c => c.Length > 0)
        && cells.All(c => c.Length == 0 || (c.Trim(':').Length > 0 && c.Trim(':').All(ch => ch == '-')));

    /// <summary>"'a', 'b'" — quoted, because a class may itself contain a comma (<c>live, uncontrolled</c>).</summary>
    private static string Quoted(IEnumerable<string> terms) =>
        string.Join(", ", terms.Order(StringComparer.Ordinal).Select(t => $"'{t}'"));

    private static bool IsCodeSpan(string cell) => cell.Length > 2 && cell[0] == '`' && cell[^1] == '`';

    private static bool CarriesIsoDate(string cell) =>
        IsoDate.Matches(cell).Any(m => DateOnly.TryParseExact(
            m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
}
