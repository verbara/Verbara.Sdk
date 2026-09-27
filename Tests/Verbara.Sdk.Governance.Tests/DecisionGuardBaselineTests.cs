namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Pins the exact-set contract of <see cref="DecisionGuardBaseline"/> (openspec change
/// <c>a-decision-is-held-by-a-test-that-can-fail</c>, design D1; spec requirement "A guard that fires
/// on an owned defect lands with a shrink-only baseline"): a site the baseline does not list fails,
/// an entry whose site no longer fires fails, and only a clean match passes. Every case runs on a
/// fixture document, so the contract holds whatever the committed baseline lists; one test reads the
/// committed file to prove it parses and names only registered guards.
/// </summary>
public sealed class DecisionGuardBaselineTests
{
    private const string Guard = "fixture-guard";

    private const string TwoSites = """
        {
          "_comment": "fixture",
          "entries": [
            { "guard": "fixture-guard", "path": "src/A/One.cs", "key": "Task.Run(() => Work(ct), ct)", "reason": "r", "owner": "change-a" },
            { "guard": "fixture-guard", "path": "src/B/Two.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "change-b" }
          ]
        }
        """;

    [Fact]
    public void Match_ShouldBeExact_WhenEverySiteIsListedAndEveryEntryFires()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/A/One.cs", 10, "Task.Run(() => Work(ct), ct)"),
            new DecisionGuardSite("src/B/Two.cs", 20, "catch (IOException) { }"),
        ]);

        match.IsExact.Should().BeTrue(match.Describe());
        match.Describe().Should().BeEmpty();
    }

    [Fact]
    public void Match_ShouldReportTheNewSite_WhenASiteFiresThatTheBaselineDoesNotList()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/A/One.cs", 10, "Task.Run(() => Work(ct), ct)"),
            new DecisionGuardSite("src/B/Two.cs", 20, "catch (IOException) { }"),
            new DecisionGuardSite("src/C/Three.cs", 30, "Task.Run(() => Other(token), token)"),
        ]);

        match.IsExact.Should().BeFalse();
        match.Unlisted.Should().ContainSingle().Which.Path.Should().Be("src/C/Three.cs");
        match.Stale.Should().BeEmpty();
        match.Describe().Should().Contain("src/C/Three.cs:30").And.Contain("Task.Run(() => Other(token), token)");
    }

    [Fact]
    public void Match_ShouldReportTheStaleEntry_WhenAListedSiteNoLongerFires()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/A/One.cs", 10, "Task.Run(() => Work(ct), ct)"),
        ]);

        match.IsExact.Should().BeFalse();
        match.Unlisted.Should().BeEmpty();
        match.Stale.Should().ContainSingle().Which.Owner.Should().Be("change-b");
        match.Describe().Should().Contain("src/B/Two.cs").And.Contain("owner: change-b");
    }

    [Fact]
    public void Match_ShouldReportEveryEntry_WhenNoSiteFiresAtAll()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard, []);

        match.Stale.Select(e => e.Path).Should().Equal("src/A/One.cs", "src/B/Two.cs");
    }

    [Fact]
    public void Match_ShouldReportBoth_WhenASiteMovesToAnotherFile()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/A/Moved.cs", 10, "Task.Run(() => Work(ct), ct)"),
            new DecisionGuardSite("src/B/Two.cs", 20, "catch (IOException) { }"),
        ]);

        match.Unlisted.Should().ContainSingle().Which.Path.Should().Be("src/A/Moved.cs");
        match.Stale.Should().ContainSingle().Which.Path.Should().Be("src/A/One.cs");
    }

    [Fact]
    public void Match_ShouldStayExact_WhenOnlyTheLineNumberAndIndentationChange()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/A/One.cs", 99, "Task.Run(\n        () => Work(ct),\n        ct)"),
            new DecisionGuardSite("src/B/Two.cs", 7, "catch (IOException)\n{\n}"),
        ]);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void Match_ShouldReportTheSecondCopy_WhenTwoIdenticalSitesShareOneEntry()
    {
        const string oneEntry = """
            { "entries": [
              { "guard": "fixture-guard", "path": "src/B/Two.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "change-b" }
            ] }
            """;
        var baseline = DecisionGuardBaseline.Parse(oneEntry);

        var match = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/B/Two.cs", 86, "catch (IOException) { }"),
            new DecisionGuardSite("src/B/Two.cs", 144, "catch (IOException) { }"),
        ]);

        match.Unlisted.Should().ContainSingle().Which.Line.Should().Be(144);
        match.Stale.Should().BeEmpty();
    }

    [Fact]
    public void Match_ShouldReportOneStaleEntry_WhenOneOfTwoIdenticalSitesIsFixed()
    {
        const string twoIdentical = """
            { "entries": [
              { "guard": "fixture-guard", "path": "src/B/Two.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "change-b" },
              { "guard": "fixture-guard", "path": "src/B/Two.cs", "key": "catch (IOException) { }", "reason": "r", "owner": "change-b" }
            ] }
            """;
        var baseline = DecisionGuardBaseline.Parse(twoIdentical);

        var bothFire = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/B/Two.cs", 86, "catch (IOException) { }"),
            new DecisionGuardSite("src/B/Two.cs", 144, "catch (IOException) { }"),
        ]);
        var oneFires = baseline.Match(Guard,
        [
            new DecisionGuardSite("src/B/Two.cs", 86, "catch (IOException) { }"),
        ]);

        bothFire.IsExact.Should().BeTrue(bothFire.Describe());
        oneFires.Unlisted.Should().BeEmpty();
        oneFires.Stale.Should().ContainSingle();
    }

    [Fact]
    public void Match_ShouldIgnoreEntriesOfOtherGuards_WhenMatchingOneGuard()
    {
        const string mixed = """
            { "entries": [
              { "guard": "fixture-guard", "path": "src/A/One.cs", "key": "k", "reason": "r", "owner": "o" },
              { "guard": "another-guard", "path": "src/A/One.cs", "key": "k", "reason": "r", "owner": "o" }
            ] }
            """;
        var baseline = DecisionGuardBaseline.Parse(mixed);

        var match = baseline.Match(Guard, [new DecisionGuardSite("src/A/One.cs", 1, "k")]);

        match.IsExact.Should().BeTrue(match.Describe());
    }

    [Fact]
    public void EntriesOfUnregisteredGuards_ShouldReportTheEntry_WhenItsGuardIsNotRegistered()
    {
        const string misspelt = """
            { "entries": [
              { "guard": "fixture-guard", "path": "src/A/One.cs", "key": "k", "reason": "r", "owner": "o" },
              { "guard": "fixture-gaurd", "path": "src/A/One.cs", "key": "k", "reason": "r", "owner": "o" }
            ] }
            """;
        var baseline = DecisionGuardBaseline.Parse(misspelt);

        var orphans = baseline.EntriesOfUnregisteredGuards(new HashSet<string>(StringComparer.Ordinal) { Guard });

        orphans.Should().ContainSingle().Which.Guard.Should().Be("fixture-gaurd");
    }

    [Theory]
    [InlineData("""{ "entries": [ { "guard": "g", "path": "src/a.cs", "key": "k", "reason": "r" } ] }""", "missing 'owner'")]
    [InlineData("""{ "entries": [ { "guard": "g", "path": "src/a.cs", "key": "k", "reason": " ", "owner": "o" } ] }""", "reason must be a non-blank string")]
    [InlineData("""{ "entries": [ { "guard": "g", "path": "src/a.cs", "key": "k", "reason": "r", "owner": "o", "line": "12" } ] }""", "unknown property 'line'")]
    [InlineData("""{ "entries": [ { "guard": "g", "path": "src\\a.cs", "key": "k", "reason": "r", "owner": "o" } ] }""", "repo-relative with forward slashes")]
    [InlineData("""{ "entries": [ { "guard": "g", "path": "/src/a.cs", "key": "k", "reason": "r", "owner": "o" } ] }""", "repo-relative with forward slashes")]
    [InlineData("""{ "_comment": "no entries" }""", "must carry an 'entries' array")]
    [InlineData("""{ "entries": [], "files": {} }""", "unknown top-level property 'files'")]
    public void Parse_ShouldThrow_WhenTheDocumentIsMalformed(string json, string expectedMessage)
    {
        var act = () => DecisionGuardBaseline.Parse(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public void Parse_ShouldReadEveryField_WhenTheEntryIsComplete()
    {
        var baseline = DecisionGuardBaseline.Parse(TwoSites);

        baseline.Entries.Should().HaveCount(2);
        baseline.Entries[0].Should().Be(new DecisionGuardBaselineEntry(
            "fixture-guard", "src/A/One.cs", "Task.Run(() => Work(ct), ct)", "r", "change-a"));
    }

    [Theory]
    [InlineData("  catch  (IOException)\t{\r\n }  ", "catch(IOException){}")]
    [InlineData("Task.Run(\n    () => Work(ct),\n    ct)", "Task.Run(()=>Work(ct),ct)")]
    [InlineData("_stoppingToken  =\n cancellationToken", "_stoppingToken=cancellationToken")]
    [InlineData("return  x", "return x")]
    [InlineData("   ", "")]
    public void NormalizeKey_ShouldKeepOnlyTokenSeparatingSpaces_WhenTheSiteIsReformatted(string text, string expected)
    {
        DecisionGuardBaseline.NormalizeKey(text).Should().Be(expected);
    }

    [Fact]
    public void NormalizeKey_ShouldKeepKeysApart_WhenOnlyATokenBoundaryDiffers()
    {
        DecisionGuardBaseline.NormalizeKey("return x").Should().NotBe(DecisionGuardBaseline.NormalizeKey("returnx"));
    }

    [Fact]
    public void DisplayKey_ShouldCollapseWhitespaceRuns_WhenQuotingASite()
    {
        DecisionGuardBaseline.DisplayKey("catch (IOException)\n        {\n        }").Should().Be("catch (IOException) { }");
    }

    [Fact]
    public void LoadCommitted_ShouldParseAndNameOnlyRegisteredGuards_WhenReadFromTheRepositoryRoot()
    {
        var baseline = DecisionGuardBaseline.LoadCommitted();

        var orphans = baseline.EntriesOfUnregisteredGuards(DecisionGuardBaseline.RegisteredGuards);

        orphans.Should().BeEmpty(
            "an entry whose guard is not registered is matched by no test, so it could never be reported " +
            "stale; register the guard in DecisionGuardBaseline.RegisteredGuards or delete the entry");
    }
}
