using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every product source file with Roslyn and fails the build if
/// a provider client type ships without a row in <c>docs/guides/provider-wire-conformance.md</c>.
/// Zero-tolerance and with no exemption mechanism at all — unlike the endpoint guard, this one has
/// nothing to excuse, because the record admits the status <c>not characterised</c> and a provider
/// nobody has measured can always say so. Includes liveness self-tests (the scan must walk a large
/// file set AND actually find the record) and detector unit tests pinning the true positive, the
/// package exclusion, the abstract-base exclusion and the column-versus-prose distinction.
/// <para>
/// A second guard reads the record alone: every client-type row carries an evidence class from the
/// record's declared vocabulary and a date (ADR-0048 D8), and every <c>wss://</c> row a validation point
/// the record declares as measured (ADR-0049 D3). It checks that a row states these, not that what it
/// states is true. Its liveness test asserts both vocabularies were read and the row counts clear a
/// floor, and its fixtures carry the rows that must be refused.
/// </para>
/// </summary>
public sealed class ConformanceRecordGuardTests
{
    // Conservative floor: a floor well below the real src file count (~864) defeats the
    // "found zero files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 500;

    // Conservative floor for the record itself: the real file is ~35 KB. A near-empty read means the
    // path resolved to the wrong thing, and every provider would then "fail" for the wrong reason —
    // or, if the check were inverted, silently pass.
    private const int MinimumRecordLength = 5000;

    private const string RecordPath = "docs/guides/provider-wire-conformance.md";

    // Liveness floors for the row check, well below today's record (14 client-type rows, 8 of them
    // WebSocket, 6 declared evidence classes). A near-zero count means the table or vocabulary reader
    // broke, and "no violations" would then be a statement about nothing. Five classes because
    // ADR-0048 D8 names five states and the record adds a sixth.
    private const int MinimumClientTypeRows = 10;
    private const int MinimumWebSocketRows = 6;
    private const int MinimumEvidenceClasses = 5;

    [Fact]
    public void Guard_ShouldRecordEveryProviderClientType_InSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        var record = File.ReadAllText(Path.Join(repoRoot, RecordPath));

        var violations = new List<UnrecordedProviderViolation>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(ConformanceRecordScanner.Scan(source, relative, record));
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src/ tree; a near-zero count means the locator broke and " +
            "the conformance-record scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldLoadTheRealRecord_WhenResolvingItsPath()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        var recordFile = Path.Join(repoRoot, RecordPath);

        File.Exists(recordFile).Should().BeTrue(
            "the conformance record must be found at '{0}'; a moved or renamed record turns this " +
            "guard into an assertion about an empty string", RecordPath);
        File.ReadAllText(recordFile).Length.Should().BeGreaterThan(
            MinimumRecordLength,
            "a near-empty record means the path resolved to the wrong file");
    }

    [Fact]
    public void Guard_ShouldFindEveryRecordedTypeInSrc_WhenWalkingBothDirections()
    {
        // The reverse direction: a row whose client type no longer exists in src/ is a row nobody
        // will ever be forced to update, and it reads as coverage of a provider that shipped away.
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        var record = File.ReadAllText(Path.Join(repoRoot, RecordPath));

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
            declared.UnionWith(ConformanceRecordScanner.DeclaredClientTypes(File.ReadAllText(file), file));

        var orphaned = ConformanceRecordScanner.RecordedClientTypes(record)
            .Where(t => !declared.Contains(t))
            .ToList();

        orphaned.Should().BeEmpty(
            "every Client type in the record must still be declared in src/; orphaned rows: {0}",
            string.Join(", ", orphaned));
    }

    [Fact]
    public void Guard_ShouldStateEvidenceDateAndValidationPoint_ForEveryClientTypeRow()
    {
        // ADR-0048 D8: a row states its evidence class and date, not only a verdict. ADR-0049 D3: a
        // WebSocket row states where the vendor validates a credential, and that point is measured.
        var check = ConformanceRecordScanner.CheckRows(ReadRecord());

        check.Violations.Should().BeEmpty(BuildRowFailureMessage(check.Violations));
    }

    [Fact]
    public void Guard_ShouldReadEveryRowAndBothVocabularies_WhenCheckingTheRealRecord()
    {
        // The row check reads its vocabularies from the record itself, so a reader that finds no
        // vocabulary, or no rows, would pass every row by inspecting none.
        var record = ReadRecord();

        ConformanceRecordScanner.DeclaredEvidenceClasses(record).Should()
            .HaveCountGreaterThanOrEqualTo(MinimumEvidenceClasses, "the 'Class' table declares the evidence vocabulary")
            .And.Contain("not characterised", "ADR-0048 D8 makes an unmeasured surface a stated class");
        ConformanceRecordScanner.DeclaredValidationPoints(record).Should()
            .Contain(["handshake", "in-band"], "the 'Validation point' list declares where a vendor can refuse a key");

        var check = ConformanceRecordScanner.CheckRows(record);

        check.ClientTypeRows.Should().BeGreaterThanOrEqualTo(
            MinimumClientTypeRows, "the check must read the two surface tables, not a table about something else");
        check.WebSocketRows.Should().BeGreaterThanOrEqualTo(
            MinimumWebSocketRows, "the validation-point rule must reach the wss:// rows it exists for");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenSynthesizerIsAbsentFromTheRecord()
    {
        const string source = "class NewVendorSpeechSynthesizer : SpeechSynthesizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "| Surface | Client type |\n");

        violations.Should().ContainSingle()
            .Which.ClientType.Should().Be("NewVendorSpeechSynthesizer");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenRecognizerIsAbsentFromTheRecord()
    {
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "");

        violations.Should().ContainSingle()
            .Which.ClientType.Should().Be("NewVendorSpeechRecognizer");
    }

    [Fact]
    public void Scan_ShouldReportOneBasedLineOfTheDeclaration_WhenTypeIsUnrecorded()
    {
        const string source =
            "namespace N;\n" +
            "\n" +
            "public sealed class NewVendorSpeechRecognizer : SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "");

        violations.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldReportTheDeclaringFile_WhenTypeIsUnrecorded()
    {
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/Pkg/Vendor/File.cs", "");

        violations.Should().ContainSingle().Which.Path.Should().Be("src/Pkg/Vendor/File.cs");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTypeIsInTheClientTypeColumn()
    {
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";
        const string record =
            "| Surface | Client type | Transport |\n" +
            "|---|---|---|\n" +
            "| New Vendor STT | `NewVendorSpeechRecognizer` | `wss://api.vendor.example` |\n";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", record);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenRowStatusIsNotCharacterised()
    {
        // 'not characterised' is a legal, passing status: the guard checks presence, never verdict.
        // The header is part of the fixture because the scanner locates the column by name; this
        // row previously stood alone, which only ever parsed because the column was read by
        // position. What the test asserts — that the verdict cell is not consulted — is unchanged.
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";
        const string record =
            "| Surface | Client type | Evidence |\n" +
            "|---|---|---|\n" +
            "| New Vendor STT | `NewVendorSpeechRecognizer` | not characterised |\n";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", record);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTypeIsOnlyMentionedInProse()
    {
        // MEASURED shape: six of the fourteen real client types are already named in backticks in
        // the record's narrative. A prose mention is not a row.
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";
        const string record =
            "The half-close defect was reproduced against `NewVendorSpeechRecognizer` on 2026-08-16.\n";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", record);

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheTypeSitsInATableThatDoesNotNameTheColumn()
    {
        // THE 2026-08-19 regression, from the other direction. The record gained a probe-results
        // table, and the positional reader took its second column for client types -- registering
        // "`101`, `transcript` then `done`" as one, because that cell begins and ends with a
        // backtick. A table about something else contributes no rows.
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";
        const string record =
            "| Surface | shipped | wrong path |\n" +
            "|---|---|---|\n" +
            "| New Vendor STT | `NewVendorSpeechRecognizer` | `404` at the upgrade |\n";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", record);

        violations.Should().ContainSingle();
    }

    [Fact]
    public void RecordedClientTypes_ShouldReadOnlyTheHeadedTable_WhenTheRecordHasSeveral()
    {
        // The reverse direction is what actually broke: cells from a results table were reported as
        // orphaned rows, which named the wrong problem in the failure message.
        const string record =
            "| Surface | Client type | Evidence |\n" +
            "|---|---|---|\n" +
            "| New Vendor STT | `NewVendorSpeechRecognizer` | `live + both controls` |\n" +
            "\n" +
            "| Surface | shipped | invalid credential |\n" +
            "|---|---|---|\n" +
            "| New Vendor STT | `101`, then `Begin` | `401` at the upgrade |\n";

        var recorded = ConformanceRecordScanner.RecordedClientTypes(record);

        recorded.Should().BeEquivalentTo(["NewVendorSpeechRecognizer"]);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTypeSitsInAColumnOtherThanClientType()
    {
        // The Surface column, or any later column, is not the Client type column.
        const string source = "class NewVendorSpeechRecognizer : SpeechRecognizer { }";
        const string record =
            "| Arm | What it sends | Outcome |\n" +
            "| shipped | `NewVendorSpeechRecognizer` as it now ships | 10/10 |\n";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", record);

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenClassIsAbstract()
    {
        // SpeechSynthesizer / SpeechRecognizer themselves, and any future abstract intermediate.
        const string source = "abstract class MiddleSpeechRecognizer : SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenClassIsInTheTestingPackage()
    {
        // In-memory doubles dial no endpoint, so they have no wire to conform to.
        const string source = "class FakeSpeechRecognizer : SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(
            source, "src/Verbara.Sdk.VoiceAi.Testing/FakeSpeechRecognizer.cs", "");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenClassDerivesFromSomethingElse()
    {
        const string source = "class NotAProvider : System.IAsyncDisposable { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenBaseTypeIsQualified()
    {
        // A fully-qualified base list must not be a way around the guard.
        const string source = "class NewVendorSpeechRecognizer : Verbara.Sdk.VoiceAi.SpeechRecognizer { }";

        var violations = ConformanceRecordScanner.Scan(source, "src/x.cs", "");

        violations.Should().ContainSingle();
    }

    // ---- Row check: evidence class, date and validation point (ADR-0048 D8, ADR-0049 D3) ----

    /// <summary>
    /// A record in the real one's shape: the evidence vocabulary as a <c>Class</c> table, the
    /// validation points as a list under <c>**Validation point**</c>, then the header of a surface
    /// table. Fifteen lines, so the first row a fixture appends sits on line 16.
    /// </summary>
    private const string RecordPreamble =
        "| Class | Means |\n" +
        "|---|---|\n" +
        "| `live + both controls` | probed live with both controls |\n" +
        "| `live, uncontrolled` | probed live with no deliberately-wrong arm |\n" +
        "| `not characterised` | nobody looked |\n" +
        "\n" +
        "**Validation point** — where the vendor decides a credential is bad:\n" +
        "\n" +
        "- `handshake` — a bad key is refused at the HTTP upgrade.\n" +
        "- `in-band` — the upgrade succeeds and the rejection arrives afterwards.\n" +
        "\n" +
        "The split is a WebSocket property.\n" +
        "\n" +
        "| Surface | Client type | Transport | Validation point | Evidence | Date |\n" +
        "|---|---|---|---|---|---|\n";

    private const int FirstRowLine = 16;

    [Fact]
    public void CheckRows_ShouldPass_WhenEveryCellIsStated()
    {
        const string record = RecordPreamble +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `wss://api.vendor.example/speak` | `handshake` | `live + both controls` | 2026-08-19 |\n" +
            "| Vendor STT | `VendorSpeechRecognizer` | `https://api.vendor.example/listen` | in the response | `live, uncontrolled` | 2026-08-09 |\n";

        var check = ConformanceRecordScanner.CheckRows(record);

        check.Violations.Should().BeEmpty();
        check.ClientTypeRows.Should().Be(2);
        check.WebSocketRows.Should().Be(1);
    }

    [Fact]
    public void CheckRows_ShouldFlag_WhenTheEvidenceIsOutsideTheDeclaredVocabulary()
    {
        const string record = RecordPreamble +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `https://api.vendor.example` | in the response | `verified` | 2026-08-19 |\n";

        var check = ConformanceRecordScanner.CheckRows(record);

        check.Violations.Should().ContainSingle()
            .Which.Should().Match<ConformanceRowViolation>(v =>
                v.Line == FirstRowLine && v.ClientType == "VendorSpeechSynthesizer" && v.Detail.Contains("verified"));
    }

    [Fact]
    public void CheckRows_ShouldFlag_WhenTheEvidenceCellIsEmpty()
    {
        const string record = RecordPreamble +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `https://api.vendor.example` | in the response |  | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle()
            .Which.Detail.Should().Contain("evidence");
    }

    [Fact]
    public void CheckRows_ShouldFlag_WhenTheEvidenceIsAVerdictInsteadOfAClass()
    {
        // The shape D8 forbids: "OK" is a verdict, and says nothing about what it rests on.
        const string record = RecordPreamble +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `https://api.vendor.example` | in the response | OK | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle();
    }

    [Fact]
    public void CheckRows_ShouldReadTheVocabularyFromTheRecord_WhenItDeclaresFewerClasses()
    {
        // The vocabulary is the record's, not a list copied into this guard: a record that declares
        // only 'documentation' refuses a row claiming a live probe.
        const string record =
            "| Class | Means |\n" +
            "|---|---|\n" +
            "| `documentation` | read from the vendor's contract |\n" +
            "\n" +
            "| Surface | Client type | Transport | Evidence | Date |\n" +
            "|---|---|---|---|---|\n" +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `https://api.vendor.example` | `live + both controls` | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle()
            .Which.Detail.Should().Contain("live + both controls");
    }

    [Theory]
    [InlineData("—")]
    [InlineData("")]
    [InlineData("August 2026")]
    [InlineData("2026-13-45")]
    public void CheckRows_ShouldFlag_WhenTheDateCellCarriesNoDate(string date)
    {
        var record = RecordPreamble +
            $"| Vendor TTS | `VendorSpeechSynthesizer` | `https://api.vendor.example` | in the response | `live + both controls` | {date} |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle()
            .Which.Detail.Should().Contain("date");
    }

    [Fact]
    public void CheckRows_ShouldPass_WhenANotCharacterisedRowCarriesTheDateItWasWritten()
    {
        // 'not characterised' is a class, and a legal one: D8 makes the unmeasured state visible
        // instead of a gap between rows. It still carries the date that was true.
        const string record = RecordPreamble +
            "| Vendor STT | `VendorSpeechRecognizer` | `wss://api.vendor.example/listen` | `handshake` | `not characterised` | 2026-08-15 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().BeEmpty();
    }

    [Fact]
    public void CheckRows_ShouldFlag_WhenAWebSocketRowSaysNotMeasured()
    {
        const string record = RecordPreamble +
            "| Vendor STT | `VendorSpeechRecognizer` | `wss://api.vendor.example/listen` | not measured | `live + both controls` | 2026-08-19 |\n";

        var check = ConformanceRecordScanner.CheckRows(record);

        check.Violations.Should().ContainSingle()
            .Which.Should().Match<ConformanceRowViolation>(v =>
                v.Line == FirstRowLine && v.Detail.Contains("not measured"));
        check.WebSocketRows.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not established")]
    [InlineData("handshake")]
    [InlineData("`at connect`")]
    public void CheckRows_ShouldFlag_WhenAWebSocketRowNamesNoDeclaredValidationPoint(string point)
    {
        // 'not established' is ADR-0049 D3's own word for a surface without the control, and it is
        // the absence of the measurement, not a point. An unquoted 'handshake' is prose, not the
        // declared term: the record writes its points in code spans, and so do its rows.
        var record = RecordPreamble +
            $"| Vendor STT | `VendorSpeechRecognizer` | `wss://api.vendor.example/listen` | {point} | `live + both controls` | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle()
            .Which.Detail.Should().Contain("validation point");
    }

    [Fact]
    public void CheckRows_ShouldPass_WhenAWebSocketRowSplitsCredentialFromSession()
    {
        // MEASURED shape, Cartesia STT: the credential is refused at the handshake and the session
        // in band. Two declared points in one cell is a measurement, and a more precise one.
        const string record = RecordPreamble +
            "| Vendor STT | `VendorSpeechRecognizer` | `wss://api.vendor.example/stt` | `handshake` (credential) + `in-band` (session) | `live + both controls` | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().BeEmpty();
    }

    [Fact]
    public void CheckRows_ShouldPass_WhenAnHttpRowSaysNotMeasured()
    {
        // The split is a WebSocket property: an HTTP surface answers in its response, so its
        // validation point is not what D3 asks for.
        const string record = RecordPreamble +
            "| Vendor STT | `VendorSpeechRecognizer` | `https://api.vendor.example/transcriptions` | not measured | `live, uncontrolled` | 2026-08-09 |\n";

        var check = ConformanceRecordScanner.CheckRows(record);

        check.Violations.Should().BeEmpty();
        check.WebSocketRows.Should().Be(0);
    }

    [Fact]
    public void CheckRows_ShouldFlagEveryRow_WhenTheTableHasNoEvidenceOrDateColumn()
    {
        // Dropping a column must not be a way around the rule: a row with no cell to read is a row
        // that states nothing.
        const string record = RecordPreamble +
            "\n" +
            "| Surface | Client type | Transport | Validation point |\n" +
            "|---|---|---|---|\n" +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `wss://api.vendor.example` | `handshake` |\n";

        var details = ConformanceRecordScanner.CheckRows(record).Violations.Select(v => v.Detail).ToList();

        details.Should().HaveCount(2);
        details.Should().Contain(d => d.Contains("evidence")).And.Contain(d => d.Contains("date"));
    }

    [Fact]
    public void CheckRows_ShouldFlag_WhenAWebSocketTableHasNoValidationPointColumn()
    {
        const string record = RecordPreamble +
            "\n" +
            "| Surface | Client type | Transport | Evidence | Date |\n" +
            "|---|---|---|---|---|\n" +
            "| Vendor TTS | `VendorSpeechSynthesizer` | `wss://api.vendor.example` | `live + both controls` | 2026-08-19 |\n";

        ConformanceRecordScanner.CheckRows(record).Violations.Should().ContainSingle()
            .Which.Detail.Should().Contain("validation point");
    }

    [Fact]
    public void CheckRows_ShouldIgnoreTablesThatDoNotNameTheClientTypeColumn()
    {
        // The probe-results and arm tables sit in the same file. A table about something else
        // carries no rows to check, exactly as it carries no rows for the presence guard.
        const string record = RecordPreamble +
            "\n" +
            "| Surface | shipped | wrong path | invalid credential |\n" +
            "|---|---|---|---|\n" +
            "| Vendor STT | `101`, then `Begin` | **`101` and a full session** | `401` at the upgrade |\n";

        var check = ConformanceRecordScanner.CheckRows(record);

        check.Violations.Should().BeEmpty();
        check.ClientTypeRows.Should().Be(0);
    }

    [Fact]
    public void DeclaredEvidenceClasses_ShouldReadTheCodeSpansOfTheClassColumn()
    {
        ConformanceRecordScanner.DeclaredEvidenceClasses(RecordPreamble).Should()
            .BeEquivalentTo(["live + both controls", "live, uncontrolled", "not characterised"]);
    }

    [Fact]
    public void DeclaredValidationPoints_ShouldReadTheListUnderTheDefinitionAndStopAtTheNextParagraph()
    {
        // A later bullet list in the record is not part of the vocabulary.
        const string record = RecordPreamble + "\n- `route` — a bullet in some later list.\n";

        ConformanceRecordScanner.DeclaredValidationPoints(record).Should()
            .BeEquivalentTo(["handshake", "in-band"]);
    }

    private static string ReadRecord()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return File.ReadAllText(Path.Join(repoRoot, RecordPath));
    }

    private static string BuildRowFailureMessage(IReadOnlyList<ConformanceRowViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" row(s) of the wire-conformance record do not state what they rest on:");
        foreach (var v in violations.OrderBy(v => v.Line))
        {
            sb.Append("  ").Append(RecordPath).Append(':').Append(v.Line)
                .Append("  ").Append(v.ClientType).Append("  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Every client-type row carries an evidence class from the record's 'Class' table and the date " +
            "it was established (ADR-0048 D8): 'not characterised' is a class and passes, with the date it " +
            "was written; a verdict is not a class. Every wss:// row names where the vendor refuses a bad " +
            "credential, as one of the points listed under 'Validation point', measured by an " +
            "invalid-credential control (ADR-0049 D3): 'not measured' is the absence of that measurement.");
        return sb.ToString();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<UnrecordedProviderViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" provider client type(s) ship without a row in the wire-conformance record:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.ClientType).Append("  ").AppendLine(v.Detail);
        }

        sb.Append("Add a row to ").Append(RecordPath).AppendLine(
            " with the type in the 'Client type' column. If nobody has measured the surface, say so: " +
            "'not characterised' is a legal status and passes this guard. What does not pass is silence.");
        return sb.ToString();
    }
}
