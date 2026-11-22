namespace Verbara.Sdk.Governance.Tests;

/// <summary>Strict Realtime metadata may reach only deserialize arguments, never a send or an alias.</summary>
public sealed class RealtimeSendContextGuardTests
{
    private const string ReadOptionsType = "RealtimeJsonReadOptions";

    [Fact]
    public void Guard_ShouldFindNoSendOnTheStrictOptions_InTheRealtimePackage()
    {
        var violations = PackageSources().SelectMany(file =>
            SttSendContextScanner.Scan(File.ReadAllText(file), file, ReadOptionsType));

        violations.Should().BeEmpty("strict options enforce nullable annotations on writes too; sends must keep their plain contexts");
    }

    [Fact]
    public void Guard_ShouldSeeTheSendAndReadSites_WhenWalkingTheRealtimePackage()
    {
        var sources = PackageSources().Select(File.ReadAllText).ToList();

        // Three request sends and two error-output writes; six typed decodes in the bridge.
        sources.Sum(SttSendContextScanner.CountSerializeCalls).Should().BeGreaterThanOrEqualTo(5);
        sources.Sum(source => SttSendContextScanner.CountStrictReads(source, ReadOptionsType))
            .Should().BeGreaterThanOrEqualTo(6, "the guard must actually see the fenced reads");
    }

    [Theory]
    [InlineData("JsonSerializer.Serialize(m, RealtimeJsonReadOptions.For<Msg>.TypeInfo)")]
    [InlineData("System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, RealtimeJsonReadOptions.StrictOptions)")]
    [InlineData("RealtimeJsonReadOptions.For<Msg>.TypeInfo")]
    public void Scan_ShouldReportTheUse_WhenRealtimeStrictOptionsCanReachASend(string expression)
    {
        var source = "class C {\n object M() => " + expression + ";\n}";

        var violations = SttSendContextScanner.Scan(source, "fixture.cs", ReadOptionsType);

        violations.Should().ContainSingle().Which.Line.Should().Be(2);
        violations[0].Path.Should().Be("fixture.cs");
    }

    [Fact]
    public void Scan_ShouldPass_WhenRealtimeStrictOptionsOnlyReachDeserializeArguments()
    {
        const string source = "class C { object M() => JsonSerializer.Deserialize(json, RealtimeJsonReadOptions.For<Msg>.TypeInfo); }";

        SttSendContextScanner.Scan(source, "fixture.cs", ReadOptionsType).Should().BeEmpty();
        SttSendContextScanner.CountStrictReads(source, ReadOptionsType).Should().Be(1);
        SttSendContextScanner.CountStrictReads(source).Should().Be(0, "STT and Realtime references are matched independently");
    }

    [Fact]
    public void Scan_ShouldPass_WhenRealtimeStrictOptionsAreNamedOnlyInCommentsOrStrings()
    {
        const string source = """
            /// <summary>RealtimeJsonReadOptions.For&lt;Msg&gt;.TypeInfo</summary>
            class C {
                // JsonSerializer.Serialize(m, RealtimeJsonReadOptions.For<Msg>.TypeInfo);
                const string S = "RealtimeJsonReadOptions";
            }
            """;

        SttSendContextScanner.Scan(source, "fixture.cs", ReadOptionsType).Should().BeEmpty();
        SttSendContextScanner.CountStrictReads(source, ReadOptionsType).Should().Be(0);
    }

    private static IEnumerable<string> PackageSources() => SrcTreeSource.EnumerateSrcSources()
        .Where(file => Path.GetRelativePath(SrcTreeSource.SrcRoot(), file)
            .Replace(Path.DirectorySeparatorChar, '/').StartsWith("Verbara.Sdk.VoiceAi.OpenAiRealtime/", StringComparison.Ordinal));
}
