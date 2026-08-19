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

    [Fact]
    public void Guard_ShouldRecordEveryProviderClientType_InSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        var record = File.ReadAllText(Path.Combine(repoRoot, RecordPath));

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
        var recordFile = Path.Combine(repoRoot, RecordPath);

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
        var record = File.ReadAllText(Path.Combine(repoRoot, RecordPath));

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
