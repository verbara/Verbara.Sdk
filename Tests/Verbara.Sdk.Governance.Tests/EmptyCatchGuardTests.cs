using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "no catch block in src is empty" (openspec change
/// <c>a-decision-is-held-by-a-test-that-can-fail</c>, spec requirement "No catch block in src is
/// empty"; ADR-0053 R3, ADR-0050 E2): no <c>catch</c> in <c>src/</c> encloses neither a statement nor
/// a comment. It fired on two sites when it landed, both owned by a change that fixes them, so they are
/// listed in <c>decision-guards-baseline.json</c> and the tree must match that list exactly: a new site
/// fails, and so does an entry whose site was fixed and left behind. Carries two liveness floors — the
/// files walked and the catch clauses inspected — and detector fixtures for every shape the scanner
/// claims to report and every shape it must pass.
/// </summary>
public sealed class EmptyCatchGuardTests
{
    // Conservative floors, well below the tree as this guard landed (~865 src files; 197 catch
    // clauses, two of them the baselined sites). The second floor is what keeps the guard honest once
    // those two are fixed and the baseline is empty: a detector that stopped finding catch clauses
    // would then pass having inspected nothing.
    private const int MinimumScannedFiles = 400;
    private const int MinimumCatchClauses = 100;

    [Fact]
    public void Guard_ShouldMatchTheBaselineExactly_WhenScanningTheSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var sites = new List<DecisionGuardSite>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
            sites.AddRange(EmptyCatchScanner.Scan(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var match = DecisionGuardBaseline.LoadCommitted().Match(EmptyCatchScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(BuildFailureMessage(match));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src tree; a near-zero count means the locator broke and " +
            "the empty-catch scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldInspectManyCatchClauses_WhenWalkingTheSrcTree()
    {
        var clauses = SrcTreeSource.EnumerateSrcSources()
            .Sum(file => EmptyCatchScanner.CountCatchClauses(File.ReadAllText(file)));

        clauses.Should().BeGreaterThanOrEqualTo(
            MinimumCatchClauses,
            "the detector must see the catch clauses src/ actually writes; finding almost none means " +
            "the clause detection broke, and an empty baseline would then pass having inspected nothing");
    }

    [Fact]
    public void GuardName_ShouldBeRegistered_WhenTheBaselineIsRead()
    {
        DecisionGuardBaseline.RegisteredGuards.Should().Contain(
            EmptyCatchScanner.GuardName,
            "an unregistered guard's baseline entries are reported as orphans, and a registration that " +
            "drifts from the scanner's name would make every entry of this guard one");
    }

    [Fact]
    public void Match_ShouldNameTheSite_WhenAFixtureSiteIsNotInTheBaseline()
    {
        const string source =
            "class Session {\n" +
            "    async Task FillAsync() {\n" +
            "        try { await Read(); }\n" +
            "        catch (IOException) { }\n" +
            "    }\n" +
            "}";
        var sites = EmptyCatchScanner.Scan(source, "src/Fixture/Session.cs");

        var match = DecisionGuardBaseline.Parse("""{ "entries": [] }""").Match(EmptyCatchScanner.GuardName, sites);

        match.IsExact.Should().BeFalse();
        match.Describe().Should().Contain("src/Fixture/Session.cs:4")
            .And.Contain("catch (IOException) { }");
    }

    [Fact]
    public void Match_ShouldReportTheThirdCopy_WhenTheBaselineListsTwoIdenticalSitesInOneFile()
    {
        // The shape the guard landed on: the same empty catch twice in one file. Each entry excuses one
        // occurrence, so a third copy in that file is a new site, not a covered one.
        const string source =
            "class Session {\n" +
            "    void A() { try { } catch (IOException) { } }\n" +
            "    void B() { try { } catch (IOException) { } }\n" +
            "    void C() { try { } catch (IOException) { } }\n" +
            "}";
        const string baseline = """
            { "entries": [
              { "guard": "empty-catch-block", "path": "src/Fixture/Session.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "o" },
              { "guard": "empty-catch-block", "path": "src/Fixture/Session.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = EmptyCatchScanner.Scan(source, "src/Fixture/Session.cs");

        var match = DecisionGuardBaseline.Parse(baseline).Match(EmptyCatchScanner.GuardName, sites);

        match.Unlisted.Should().ContainSingle().Which.Line.Should().Be(4);
        match.Stale.Should().BeEmpty();
    }

    [Fact]
    public void Match_ShouldBeExact_WhenTheBaselineListsAMultiLineSiteAsItIsQuoted()
    {
        // The key is pasted from the failure message into the entry, so the scanner's key and the
        // matcher's normalization must agree on a site written across several lines.
        const string source =
            "class Session {\n" +
            "    void A() {\n" +
            "        try { Read(); }\n" +
            "        catch (IOException)\n" +
            "        {\n" +
            "        }\n" +
            "    }\n" +
            "}";
        const string baseline = """
            { "entries": [
              { "guard": "empty-catch-block", "path": "src/Fixture/Session.cs",
                "key": "catch (IOException) { }", "reason": "r", "owner": "o" }
            ] }
            """;
        var sites = EmptyCatchScanner.Scan(source, "src/Fixture/Session.cs");

        var match = DecisionGuardBaseline.Parse(baseline).Match(EmptyCatchScanner.GuardName, sites);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheBlockIsEmptyBraces()
    {
        const string source =
            "class C {\n" +
            "    void M() {\n" +
            "        try { Work(); }\n" +
            "        catch (IOException) { }\n" +
            "    }\n" +
            "}";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Should().Be(new DecisionGuardSite("x.cs", 4, "catch (IOException) { }"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheBlockHoldsOnlyWhitespaceAndLineBreaks()
    {
        const string source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            "    catch (System.IO.IOException ex)\n" +
            "    {\n" +
            "        \t  \n" +
            "\n" +
            "    }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Line.Should().Be(3);
        DecisionGuardBaseline.NormalizeKey(sites[0].Key).Should().Be("catch(System.IO.IOException ex){}");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheCatchHasNoDeclaration()
    {
        const string source =
            "class C { void M() {\n" +
            "    try { Work(); } catch { }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("catch { }");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAFilteredCatchIsEmpty()
    {
        // The filter narrows what is swallowed; it does not say why, and the key keeps it so two empty
        // catches that differ only in their filter stay two sites.
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    try { Work(); }\n" +
            "    catch (IOException) when (ct.IsCancellationRequested) { }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("catch (IOException) when (ct.IsCancellationRequested) { }");
    }

    [Theory]
    [InlineData("{ ; }")]
    [InlineData("{ { } }")]
    [InlineData("{ ;; { ; } }")]
    public void Scan_ShouldFlag_WhenTheBlockHoldsOnlyEmptyStatementsOrEmptyBlocks(string block)
    {
        var source =
            "class C { void M() {\n" +
            $"    try {{ Work(); }} catch (IOException) {block}\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOnlyCommentIsOutsideTheBraces()
    {
        const string source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            "    // the peer left; the read loop reports it\n" +
            "    catch (IOException) { } // the peer left\n" +
            "    try { Work(); }\n" +
            "    catch (TimeoutException) /* why */ { }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Select(s => s.Line).Should().Equal(4, 6);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheEmptyCatchIsNestedInsideANonEmptyOne()
    {
        const string source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            "    catch (IOException)\n" +
            "    {\n" +
            "        try { Cleanup(); } catch (ObjectDisposedException) { }\n" +
            "    }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().ContainSingle().Which.Key.Should().Be("catch (ObjectDisposedException) { }");
        EmptyCatchScanner.CountCatchClauses(source).Should().Be(2);
    }

    [Theory]
    [InlineData("{ /* the peer left; the read loop reports it */ }")]
    [InlineData("{\n        // the peer left; the read loop reports it\n    }")]
    [InlineData("{\n        /// the peer left\n    }")]
    [InlineData("{ ; // Best effort\n }")]
    public void Scan_ShouldPass_WhenTheBlockHoldsOnlyAComment(string block)
    {
        var source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            $"    catch (IOException) {block}\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        EmptyCatchScanner.CountCatchClauses(source).Should().Be(1, "the clause was inspected and passed");
    }

    [Theory]
    [InlineData("{ throw; }")]
    [InlineData("{ return; }")]
    [InlineData("{ _log.Warn(ex); }")]
    [InlineData("{ { Reset(); } }")]
    public void Scan_ShouldPass_WhenTheBlockHoldsAStatement(string block)
    {
        var source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            $"    catch (IOException ex) {block}\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenAFilteredCatchHoldsAStatementOrAComment()
    {
        const string source =
            "class C { void M(System.Threading.CancellationToken ct) {\n" +
            "    try { Work(); }\n" +
            "    catch (IOException) when (ct.IsCancellationRequested) { throw; }\n" +
            "    try { Work(); }\n" +
            "    catch (IOException) when (ct.IsCancellationRequested) { /* stopping: the loop reports it */ }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        EmptyCatchScanner.CountCatchClauses(source).Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheBodyIsConditionallyCompiled()
    {
        // Parsed with no symbols, the throw is disabled text and the block has no statement; under
        // DEBUG it has one. A syntactic scan cannot know which configuration ships, so it does not
        // report a block that is empty only under the symbols it parsed with.
        const string source =
            "class C { void M() {\n" +
            "    try { Work(); }\n" +
            "    catch (IOException)\n" +
            "    {\n" +
            "#if DEBUG\n" +
            "        throw;\n" +
            "#endif\n" +
            "    }\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenAnEmptyCatchIsOnlyMentionedInCommentsOrStrings()
    {
        const string source =
            "/// <c>catch (IOException) { }</c> is the defect.\n" +
            "class C { void M() {\n" +
            "    // try { Work(); } catch (IOException) { }\n" +
            "    var s = \"try { Work(); } catch (IOException) { }\";\n" +
            "} }";

        var sites = EmptyCatchScanner.Scan(source, "x.cs");

        sites.Should().BeEmpty();
        EmptyCatchScanner.CountCatchClauses(source).Should().Be(0);
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(DecisionGuardMatch match)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "ADR-0053 R3 / ADR-0050 E2: no catch block in src/ is empty. An empty catch makes a transport " +
            "failure indistinguishable from a clean ending, and whatever runs next cannot tell a peer that " +
            "left from a socket that broke.");
        sb.AppendLine(
            "Act on the exception, or write inside the braces why it is ignored: " +
            "catch (IOException) { /* the peer left; the read loop reports it */ }.");
        sb.Append(match.Describe());
        return sb.ToString();
    }
}
