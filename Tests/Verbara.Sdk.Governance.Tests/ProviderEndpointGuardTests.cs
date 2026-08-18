using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every product source file with Roslyn and fails the build if
/// a provider's production endpoint is written inline at a call site instead of being declared once
/// — in the provider's options type or a single named constant. Like the reflection ban and the
/// loopback-seam guard, and unlike the sync-fence ratchet, this guard is zero-tolerance for
/// UNMARKED sites; the two that remain carry an inline <c>// endpoint-allow:</c> marker and are
/// counted by an exact-count ratchet so a third cannot appear without a reviewer seeing the number
/// change. Includes a liveness self-test (the scan must actually walk a large file set) and detector
/// unit tests pinning both the true positives and the immunity of declarations, loopback seams,
/// bare scheme tokens and prose.
/// </summary>
public sealed class ProviderEndpointGuardTests
{
    // Conservative floor: a floor well below the real src file count (~864) defeats the
    // "found zero files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 500;

    // Exact exemption tally, measured 2026-08-18 against the current tree. NEVER raise this to make
    // a build pass: raising it is the reviewable act, and the reason belongs in the marker at the
    // site. Lower it by hand when a site is remediated. The two sites are —
    //   src/Verbara.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs   REGION-TEMPLATED
    //   src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs        PENDING-VERIFICATION
    private const int ExpectedExemptions = 2;

    [Fact]
    public void Guard_ShouldFindNoInlinedProviderEndpoints_InSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var violations = new List<ProviderEndpointViolation>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(ProviderEndpointScanner.Scan(source, relative));
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
            "the endpoint scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldCarryExactlyTheRecordedExemptions_InSrcTree()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;

        var exempted = new List<ProviderEndpointViolation>();
        foreach (var file in SrcTreeSource.EnumerateSrcSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            exempted.AddRange(ProviderEndpointScanner.ScanExempted(source, relative));
        }

        exempted.Should().HaveCount(
            ExpectedExemptions,
            "the exemption tally is a ratchet: {0} marked site(s) found —\n{1}",
            exempted.Count,
            string.Join("\n", exempted.Select(v => $"  {v.Path}:{v.Line}  {v.Endpoint}")));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenEndpointLiteralIsReturnedFromAMethod()
    {
        const string source =
            "class C { System.Uri M() => new System.Uri(\"wss://api.vendor.example/v1/stream\"); }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle()
            .Which.Endpoint.Should().Be("wss://api.vendor.example/v1/stream");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenEndpointIsInterpolatedFromAnOption()
    {
        const string source =
            "class C { string Region = \"eastus\";\n" +
            "  string M() { return $\"https://{Region}.tts.speech.microsoft.com\"; } }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle()
            .Which.Endpoint.Should().Be("https://{}.tts.speech.microsoft.com");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenEndpointIsALocalVariableInsideAMethod()
    {
        const string source =
            "class C { void M() { var origin = \"https://api.vendor.example\"; } }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldReportOneBasedLine_WhenEndpointIsNotOnFirstLine()
    {
        const string source =
            "class C {\n" +
            "    System.Uri M() =>\n" +
            "        new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "}";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldReportTheGivenPath_WhenSiteIsFound()
    {
        const string source = "class C { void M() { var u = \"https://api.vendor.example\"; } }";

        var violations = ProviderEndpointScanner.Scan(source, "src/Some/File.cs");

        violations.Should().ContainSingle().Which.Path.Should().Be("src/Some/File.cs");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEndpointIsANamedConstant()
    {
        // The shape LmntSpeechSynthesizer and GoogleSpeechRecognizer now use.
        const string source = "class C { private const string Origin = \"https://api.vendor.example\"; }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEndpointIsAStaticReadonlyUriField()
    {
        // The shape OpenAiRealtimeBridge uses: the literal sits inside a constructor call that is
        // itself the field's initializer.
        const string source =
            "class C { private static readonly System.Uri Default = new(\"wss://api.vendor.example/v1\"); }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEndpointIsAnOptionsPropertyInitializer()
    {
        // The shape every *Options.cs in this repo uses.
        const string source =
            "class C { public string BaseUri { get; set; } = \"wss://api.vendor.example/v1/listen\"; }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenHostIsLoopback()
    {
        // A fake-server seam is LoopbackSeamScanner's business, not this guard's.
        const string source =
            "class C { System.Uri M(int p) => new System.Uri($\"ws://127.0.0.1:{p}/v1/stream\"); }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSchemeCarriesNoHost()
    {
        // AriClient derives its WebSocket origin this way; a bare scheme names no endpoint.
        const string source =
            "class C { string M(string u) => u.Replace(\"http://\", \"ws://\", System.StringComparison.Ordinal); }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenAttributeErrorMessageQuotesTwoSchemes()
    {
        // MEASURED false positive, not a hypothetical: the first run of this scanner over src/
        // reported ten sites, of which eight were exactly this shape. Both quoted schemes must be
        // rejected, which is why the detector examines every occurrence and not just the first.
        const string source =
            "class C {\n" +
            "  [System.ComponentModel.DataAnnotations.RegularExpression(@\"^wss?://.+\",\n" +
            "     ErrorMessage = \"BaseUri must start with wss:// or ws://.\")]\n" +
            "  public string BaseUri { get; set; } = \"\";\n" +
            "}";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSchemeIsFollowedByASpace()
    {
        const string source =
            "class C { void M() { var s = \"send it to https:// whatever host you like\"; } }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEndpointIsInComment()
    {
        const string source =
            "class C { void M() { /* was \"https://api.vendor.example\" before the fix */ } }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEndpointIsInXmlDoc()
    {
        // This repo cites vendor documentation heavily; every such link lives in XML-doc trivia.
        const string source =
            "/// <see href=\"https://docs.vendor.example/rt-api-ref\"/>\n" +
            "class C { void M() { } }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerIsBare()
    {
        // A bare marker is not an exemption — the category and the reason are mandatory.
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow:\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerCategoryIsUnknown()
    {
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: BECAUSE-I-SAID-SO — trust me\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerReasonIsEmpty()
    {
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: REGION-TEMPLATED —\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenValidMarkerSitsOnThePrecedingLine()
    {
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: PENDING-VERIFICATION — route not verified live; see the record\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenValidMarkerSitsHigherInTheSameCommentBlock()
    {
        // The reason for an endpoint exemption does not fit on one line, so the marker is looked for
        // anywhere in the unbroken comment run directly above the site. Both real exemptions in
        // src/ are written this way.
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: REGION-TEMPLATED — the origin is interpolated from an option and\n" +
            "    // the options type exposes no endpoint property, so no single constant can hold it.\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerIsSeparatedFromSiteByCode()
    {
        // A marker must not reach across intervening code and excuse a site it does not belong to.
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: REGION-TEMPLATED — belongs to the line below this one\n" +
            "    var other = 1;\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var violations = ProviderEndpointScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(4);
    }

    [Fact]
    public void ScanExempted_ShouldReportTheSite_WhenMarkerIsValid()
    {
        const string source =
            "class C { System.Uri M() {\n" +
            "    // endpoint-allow: PENDING-VERIFICATION — route not verified live; see the record\n" +
            "    return new System.Uri(\"wss://api.vendor.example/v1\");\n" +
            "} }";

        var exempted = ProviderEndpointScanner.ScanExempted(source, "x.cs");

        exempted.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void ScanExempted_ShouldReportNothing_WhenMarkerHasNoSiteBehindIt()
    {
        // A marker cannot pad the ratchet: it counts only when it excuses a real site.
        const string source =
            "class C { void M() {\n" +
            "    // endpoint-allow: REGION-TEMPLATED — nothing here\n" +
            "} }";

        var exempted = ProviderEndpointScanner.ScanExempted(source, "x.cs");

        exempted.Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<ProviderEndpointViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" production provider endpoint(s) written inline at a call site:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.Endpoint).Append("  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Declare the endpoint once — in the provider's options type (the shape every *Options.cs " +
            "here uses) or a single named constant — so configuration can reach it and a reviewer can " +
            "audit it against the conformance record. If it genuinely cannot be hoisted, annotate the " +
            "site with '// endpoint-allow: <REGION-TEMPLATED|PENDING-VERIFICATION> — <reason>' and " +
            "raise ExpectedExemptions in ProviderEndpointGuardTests, which is the reviewable act.");
        return sb.ToString();
    }
}
