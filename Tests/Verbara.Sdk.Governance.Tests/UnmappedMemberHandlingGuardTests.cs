using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for the tolerate-unknown-members default of every provider reply: no VoiceAi package sets
/// <c>JsonUnmappedMemberHandling.Disallow</c> on a provider DTO, a serializer context or a
/// <c>JsonSerializerOptions</c>. A vendor that adds a member to a reply must not break a released SDK.
/// </summary>
/// <remarks>
/// The strict read options the provider packages build turn null enforcement on, and a later
/// "harden the parser" edit could turn this on beside it in one line; every behaviour test would stay
/// green, because none sends a member the DTO does not declare. This guard is a text scanner: it proves
/// which value the source names, not what an options object does at runtime (see
/// <see cref="UnmappedMemberHandlingScanner"/>).
/// </remarks>
public sealed class UnmappedMemberHandlingGuardTests
{
    // The scope holds 116 source files today (VoiceAi 25, AudioSocket 15, OpenAiRealtime 18, Stt 21,
    // Testing 4, Tts 23, TurnDetection 10), three serializer contexts (Stt, Tts, OpenAiRealtime) and four
    // JsonSerializerOptions constructions (one strict read options per provider package, plus the realtime
    // bridge's readable output context). Conservative floors, so a broken locator or matcher fails here
    // instead of reporting a clean scan of nothing.
    private const int MinimumScannedFiles = 80;
    private const int MinimumSerializerContexts = 3;
    private const int MinimumOptionsConstructions = 4;

    [Fact]
    public void Guard_ShouldFindNoDisallowedUnmappedMembers_InTheVoiceAiPackages()
    {
        var violations = ScopeSources()
            .SelectMany(file => UnmappedMemberHandlingScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldSeeTheProviderContextsAndOptions_WhenWalkingTheVoiceAiPackages()
    {
        var files = ScopeSources().ToList();
        var sources = files.Select(file => File.ReadAllText(file.FullPath)).ToList();

        files.Count.Should().BeGreaterThan(MinimumScannedFiles,
            "the guard must walk the VoiceAi packages; a near-zero count means the locator or the scope broke");
        sources.Sum(UnmappedMemberHandlingScanner.CountSerializerContexts).Should().BeGreaterThanOrEqualTo(
            MinimumSerializerContexts,
            "the guard must see the Stt, Tts and OpenAiRealtime serializer contexts it protects");
        sources.Sum(UnmappedMemberHandlingScanner.CountOptionsConstructions).Should().BeGreaterThanOrEqualTo(
            MinimumOptionsConstructions,
            "the guard must see the JsonSerializerOptions the provider packages build");
    }

    [Fact]
    public void Scan_ShouldReportTheLine_WhenAContextSetsDisallowInItsGenerationOptions()
    {
        const string source =
            "namespace Fixture;\n" +
            "[JsonSerializable(typeof(Reply))]\n" +
            "[JsonSourceGenerationOptions(\n" +
            "    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,\n" +
            "    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]\n" +
            "internal sealed partial class C : JsonSerializerContext;\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "src/Verbara.Sdk.VoiceAi.Stt/C.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Line.Should().Be(5);
        violation.Form.Should().Contain("JsonSourceGenerationOptions");
        BuildFailureMessage(violations.ToList()).Should().Contain("src/Verbara.Sdk.VoiceAi.Stt/C.cs:5");
    }

    [Fact]
    public void Scan_ShouldReportTheLine_WhenADtoCarriesTheTypeLevelAttribute()
    {
        const string source =
            "using System.Text.Json.Serialization;\n" +
            "[System.Text.Json.Serialization.JsonUnmappedMemberHandlingAttribute(\n" +
            "    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]\n" +
            "internal sealed class Reply { public string? Text { get; init; } }\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "x.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Line.Should().Be(3);
        violation.Form.Should().Contain("on a type");
    }

    [Fact]
    public void Scan_ShouldReportTheLine_WhenOptionsAreInitializedWithDisallow()
    {
        const string source =
            "static class C {\n" +
            "    static readonly JsonSerializerOptions Strict = new(Ctx.Default.Options)\n" +
            "    {\n" +
            "        RespectNullableAnnotations = true,\n" +
            "        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,\n" +
            "    };\n" +
            "}\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "x.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Line.Should().Be(5);
        violation.Form.Should().Contain("JsonSerializerOptions");
    }

    [Fact]
    public void Scan_ShouldReportTheLine_WhenOptionsAreAssignedDisallowAfterConstruction()
    {
        const string source =
            "static class C {\n" +
            "    static JsonSerializerOptions M() {\n" +
            "        var options = new JsonSerializerOptions();\n" +
            "        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;\n" +
            "        return options;\n" +
            "    }\n" +
            "}\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(4);
    }

    [Fact]
    public void Scan_ShouldReportTheValue_WhenItIsHeldBeforeReachingAnOptionsObject()
    {
        const string source =
            "static class C {\n" +
            "    const JsonUnmappedMemberHandling Handling = JsonUnmappedMemberHandling.Disallow;\n" +
            "}\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldReportTheSpelling_WhenACastOrAUsingDirectiveHidesTheValuesName()
    {
        const string source =
            "using static System.Text.Json.Serialization.JsonUnmappedMemberHandling;\n" +
            "using Handling = System.Text.Json.Serialization.JsonUnmappedMemberHandling;\n" +
            "static class C {\n" +
            "    static readonly object H = (JsonUnmappedMemberHandling)1;\n" +
            "}\n";

        var violations = UnmappedMemberHandlingScanner.Scan(source, "x.cs");

        violations.Select(v => v.Line).Should().Equal(1, 2, 4);
    }

    [Fact]
    public void Scan_ShouldReportNothing_WhenTheDefaultIsKeptOrSkipIsNamed()
    {
        const string source =
            "using System.Text.Json.Serialization;\n" +
            "[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]\n" +
            "internal sealed partial class C : JsonSerializerContext;\n" +
            "static class D {\n" +
            "    static readonly JsonSerializerOptions O = new() { RespectNullableAnnotations = true };\n" +
            "}\n";

        UnmappedMemberHandlingScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldReportNothing_WhenDisallowIsOnlyNamedInACommentAnXmlDocOrAString()
    {
        const string source =
            "/// <summary>Never set <c>JsonUnmappedMemberHandling.Disallow</c> here.</summary>\n" +
            "static class C {\n" +
            "    // UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow would break on a new vendor field\n" +
            "    const string Note = \"UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow\";\n" +
            "    /* (JsonUnmappedMemberHandling)1 */\n" +
            "}\n";

        UnmappedMemberHandlingScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Theory]
    [InlineData("src/Verbara.Sdk.VoiceAi/VoiceAiPipeline.cs", true)]
    [InlineData("src/Verbara.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs", true)]
    [InlineData("src/Verbara.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs", true)]
    [InlineData("src/Verbara.Sdk.VoiceAi.OpenAiRealtime/Internal/RealtimeJsonContext.cs", true)]
    [InlineData("src/Verbara.Sdk.Ami/Internal/AmiJsonContext.cs", false)]
    [InlineData("src/Verbara.Sdk.Ari/AriJsonContext.cs", false)]
    [InlineData("src/Verbara.Sdk.Agi/Agi.cs", false)]
    [InlineData("src/Verbara.Sdk.Sessions/SessionJsonContext.cs", false)]
    [InlineData("src/Verbara.Sdk.VoiceAiTools/X.cs", false)]
    public void IsInScope_ShouldHoldOnlyTheVoiceAiPackages_WhenGivenARepoRelativePath(string relativePath, bool expected)
    {
        UnmappedMemberHandlingScanner.IsInScope(relativePath).Should().Be(expected);
    }

    [Fact]
    public void Scan_ShouldThrow_WhenAnArgumentIsNull()
    {
        var nullSource = () => UnmappedMemberHandlingScanner.Scan(null!, "x.cs");
        var nullPath = () => UnmappedMemberHandlingScanner.Scan("class C { }", null!);

        nullSource.Should().Throw<ArgumentNullException>();
        nullPath.Should().Throw<ArgumentNullException>();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> ScopeSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => UnmappedMemberHandlingScanner.IsInScope(file.RelativePath));
    }

    private static string BuildFailureMessage(List<UnmappedMemberViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" place(s) in a VoiceAi package where a provider reply with an unknown member would fail to read:");
        foreach (var v in violations)
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Form);

        sb.AppendLine(
            "Tolerating unknown members is the deliberate default for every provider reply: a vendor that adds " +
            "a field must not break a released SDK. This guard exists because a \"harden the parser\" edit is " +
            "exactly how that protection gets deleted. Remove the Disallow; hold a member to its contract with " +
            "a nullable annotation or a required member instead.");
        sb.AppendLine(
            "Limit: this is a text scanner. It proves which value the source names, not what an options object " +
            "does at runtime; an options object built elsewhere and handed in is invisible to it.");
        return sb.ToString();
    }
}
