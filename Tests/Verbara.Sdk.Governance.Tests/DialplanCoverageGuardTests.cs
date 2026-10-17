using Xunit.Abstractions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Every extension the functional suite dials into <c>[test-functional]</c> is defined there, through an include, or by
/// a parking lot <c>res_parking.conf</c> registers in an included context. A dial into a missing extension makes Asterisk
/// refuse the call without a word, and a test that waits for an event and returns when none arrives then passes having
/// checked nothing. The guard reads every spelling of a dial the suite uses and fails, naming the file and line, on a
/// dial it cannot resolve as well as on one it resolves to a missing number.
/// </summary>
public sealed class DialplanCoverageGuardTests(ITestOutputHelper output)
{
    private const string Test = DialplanCoverageScanner.SuitePath + "/Layer5_Integration/Some/SomeTests.cs";

    // GreenTree wraps a fixture body in a class and a method, so the body's first line is the file's fifth.
    private const int BodyOffset = 4;

    private const string GreenDialplan = """
        [default]
        exten => _X.,1,Answer()

        [test-functional]
        include => parkedcalls
        exten => 100,1,Answer()
         same => n,Wait(30)
        exten => _2XX,1,Answer() ; a pattern
        exten => 300/1234,1,Answer()
        """;

    private const string GreenParking = """
        [general]

        [default]
        context = parkedcalls
        parkext = 750
        parkpos = 751-770
        """;

    private const string OtherTest = DialplanCoverageScanner.SuitePath + "/Layer5_Integration/Other/OtherTests.cs";

    private static readonly IReadOnlyList<DialplanCoverageScanner.DeliberateDial> NoUndefined = [];

    [Fact]
    public void Tree_ShouldDefineEveryExtensionTheFunctionalSuiteDials_WhenScanned()
    {
        var report = DialplanCoverageScanner.Scan(RepoRoot());
        output.WriteLine(report.ToString());
        foreach (var dial in report.Dials.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line))
            output.WriteLine($"{dial} -> {(dial.Extensions is null ? "(unresolvable)" : string.Join(",", dial.Extensions))}");

        report.IsClean.Should().BeTrue(
            "every extension a functional test dials into [test-functional] must be defined there, and every dial must be "
            + "resolvable or listed with its reason. Found:" + Environment.NewLine + report);
    }

    [Fact]
    public void Tree_ShouldReadEverySpellingOfADial_WhenScanned()
    {
        var report = DialplanCoverageScanner.Scan(RepoRoot());

        report.Dials.Select(d => d.Form).Distinct().Should().BeEquivalentTo(
            [
                DialplanCoverageScanner.Forms.LocalChannel,
                DialplanCoverageScanner.Forms.InterpolatedLocalChannel,
                DialplanCoverageScanner.Forms.ContextExten,
                DialplanCoverageScanner.Forms.PositionalArgument,
                DialplanCoverageScanner.Forms.NamedArgument,
            ],
            "the suite dials in all five spellings; a spelling the scan stopped finding would pass on nothing");
        report.Dials.Count.Should().BeGreaterThan(150, "the suite holds well over 150 dials; a near-zero count means the walk broke");
        report.Defined.Should().Contain(["100", "700", "750", "751", "770"], "the dialplan and the parking lot must both be read");
    }

    [Fact]
    public void Scan_ShouldPass_WhenEveryDialIsDefined()
    {
        using var tree = GreenTree("""
            var a = new OriginateAction { Channel = "Local/100@test-functional/n", Context = "test-functional", Exten = "250" };
            var b = new RedirectAction { Channel = channel, Context = "test-functional", Exten = "751" };
            await server.OriginateAsync(channel, "test-functional", "300");
            await rig.OriginateAsync("Local/750@test-functional", context: "test-functional", exten: "100");
            name.StartsWith("Local/100@test-functional-", StringComparison.Ordinal);
            """);

        var report = Scan(tree);

        report.IsClean.Should().BeTrue(report.ToString());
        report.Dials.Should().HaveCount(7);
    }

    [Theory]
    [InlineData("var a = new OriginateAction { Channel = \"Local/160@test-functional\", Application = \"Wait\" };", 1, "160")]
    [InlineData("var a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = \"161\" };", 1, "161")]
    [InlineData("var a = new AtxferAction { Channel = c,\n    Context = \"test-functional\",\n    Exten = \"162\" };", 3, "162")]
    [InlineData("await server.OriginateAsync(channel, \"test-functional\", \"163\");", 1, "163")]
    [InlineData("await rig.OriginateAsync(channel, context: \"test-functional\", exten: \"164\");", 1, "164")]
    [InlineData("const string Exten = \"165\";\nvar a = $\"Local/{Exten}@test-functional\";", 2, "165")]
    [InlineData("var a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = \"7\" + \"71\" };", 1, "771")]
    public void Scan_ShouldNameFileAndLine_WhenADialReachesAnUndefinedExtension(string body, int line, string extension)
    {
        using var tree = GreenTree(body);

        var report = Scan(tree);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{Test}:{line + BodyOffset}: ", StringComparison.Ordinal)
            && v.Contains($"dials {extension},", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldResolveAHelperParameter_FromEveryCallSiteInTheFile()
    {
        using var tree = GreenTree("""
            await DialAsync("100");
            await DialAsync("772");
            Task DialAsync(string exten) => Send(new OriginateAction { Channel = $"Local/{exten}@test-functional" });
            """);

        var report = Scan(tree);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{Test}:{3 + BodyOffset}: ", StringComparison.Ordinal)
            && v.Contains("dials 772,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var slot = parked.Exten ?? \"751\";\nvar a = $\"Local/{slot}@test-functional\";", 2)]
    [InlineData("var a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = parked.Exten };", 1)]
    [InlineData("await server.OriginateAsync(channel, \"test-functional\", Pick());", 1)]
    [InlineData("await rig.OriginateAsync(channel, context: \"test-functional\");", 1)]
    [InlineData("var exten = \"100\";\nexten = Pick();\nvar a = $\"Local/{exten}@test-functional\";", 3)]
    [InlineData("var exten = \"100\";\nif (late) { exten += \"1\"; }\nvar a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = exten };", 3)]
    [InlineData("var exten = \"100\";\nRefill(ref exten);\nvar a = $\"Local/{exten}@test-functional\";", 3)]
    [InlineData("var exten = \"100\";\nvar ok = TryPick(out exten);\nvar a = $\"Local/{exten}@test-functional\";", 3)]
    public void Scan_ShouldFailNamingTheSite_WhenADialCannotBeResolved(string body, int line)
    {
        using var tree = GreenTree(body);

        var report = Scan(tree);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{Test}:{line + BodyOffset}: ", StringComparison.Ordinal)
            && v.Contains("cannot be resolved statically", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnUnresolvableDialIsListedWithItsReason()
    {
        using var tree = GreenTree("var slot = parked.Exten ?? \"751\";\nvar a = $\"Local/{slot}@test-functional\";");

        var report = DialplanCoverageScanner.Scan(tree.Root, NoUndefined, [new(Test, "slot", "a run-time parking space")]);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldResolveALocal_WhenOnlyAnInitializerMemberSharesItsName()
    {
        using var tree = GreenTree("var Exten = \"100\";\nvar a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = Exten };");

        var report = Scan(tree);

        report.IsClean.Should().BeTrue(report.ToString());
        report.Dials.Should().ContainSingle().Which.Extensions.Should().Equal("100");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnUndefinedExtensionIsListedAsDeliberate()
    {
        using var tree = GreenTree("var a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = \"9999\" };");

        var report = DialplanCoverageScanner.Scan(tree.Root, [new(Test, "9999", "does not exist on purpose")], []);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldNameTheSite_WhenADeliberatelyUndefinedNumberIsDialledFromAnotherFile()
    {
        using var tree = GreenTree("var a = new RedirectAction { Channel = c, Context = \"test-functional\", Exten = \"9999\" };");
        tree.Write(OtherTest, "class OtherTests\n{\n    async Task M()\n    {\nvar a = \"Local/9999@test-functional\";\n    }\n}\n");

        var report = DialplanCoverageScanner.Scan(tree.Root, [new(Test, "9999", "does not exist on purpose")], []);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle()
            .Which.Should().StartWith($"{OtherTest}:{1 + BodyOffset}: ").And.Contain("dials 9999,");
    }

    [Fact]
    public void Scan_ShouldNotCountANamedAttributeArgument_AsADial()
    {
        using var tree = GreenTree("var a = \"Local/100@test-functional\";");
        tree.Write(OtherTest,
            "class OtherTests\n{\n    [Fact(Skip = \"an originate to Local/9998@test-functional produced no Cdr\")]\n    void M() { }\n}\n");

        var report = DialplanCoverageScanner.Scan(tree.Root, [new(OtherTest, "9998", "kept only by the Skip reason")], []);
        output.WriteLine(report.ToString());

        report.Dials.Should().NotContain(d => d.Path == OtherTest, "a Skip reason never reaches the test body, so it dials nothing");
        report.Violations.Should().ContainSingle()
            .Which.Should().Be($"{OtherTest}: 9998 is listed as deliberately undefined, but no dial there reaches it: remove the entry");
    }

    [Fact]
    public void Scan_ShouldCountAPositionalAttributeArgument_AsADial()
    {
        using var tree = GreenTree("var a = \"Local/100@test-functional\";");
        tree.Write(OtherTest,
            "class OtherTests\n{\n    [Theory]\n    [InlineData(\"Local/9997@test-functional\")]\n    void M(string channel) { }\n}\n");

        var report = Scan(tree);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle()
            .Which.Should().StartWith($"{OtherTest}:4: ").And.Contain("dials 9997,");
    }

    [Fact]
    public void Scan_ShouldReportStaleEntries_WhenAnAllowlistNoLongerMatchesTheTree()
    {
        using var tree = GreenTree("var a = \"Local/100@test-functional\";");

        var report = DialplanCoverageScanner.Scan(tree.Root,
            [new(Test, "9999", "gone"), new(Test, "100", "now defined")],
            [new(Test, "slot", "gone")]);
        output.WriteLine(report.ToString());

        report.Violations.Should().Contain(v => v.StartsWith($"{Test}: 9999 is listed as deliberately undefined, but no dial there reaches it", StringComparison.Ordinal))
            .And.Contain(v => v.StartsWith($"{Test}: 100 is listed as deliberately undefined, but [test-functional] defines it", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("'slot' is listed as unresolvable, but no dial there", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var a = \"PJSIP/100@test-functional\";")]
    [InlineData("if (evt.Context == \"test-functional\") { }")]
    [InlineData("var a = $\"{tech}/100@test-functional\";")]
    [InlineData("var a = \"Local/100@test-functional-other\";")]
    public void Scan_ShouldFailNamingTheSite_WhenTheContextIsMentionedInAShapeItDoesNotRead(string body)
    {
        using var tree = GreenTree(body);

        var report = Scan(tree);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{Test}:{1 + BodyOffset}: ", StringComparison.Ordinal)
            && v.Contains("in a shape the dialplan guard does not read", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldIgnoreComments_WhenTheyMentionADial()
    {
        using var tree = GreenTree("// Local/999@test-functional\n/// <c>Local/998@test-functional</c>\nvar a = \"Local/100@test-functional\";");

        var report = Scan(tree);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldReportAnIncludeNothingDefines()
    {
        using var tree = GreenTree("var a = \"Local/100@test-functional\";");
        tree.Write(DialplanCoverageScanner.ParkingPath, "[general]\n");

        var report = Scan(tree);

        report.Violations.Should().ContainSingle(v => v.StartsWith("[parkedcalls] is included", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReportFoundNoDial_WhenTheSuiteIsEmpty()
    {
        using var tree = new FixtureTree();
        tree.Write(DialplanCoverageScanner.DialplanPath, GreenDialplan);

        var report = Scan(tree);

        report.Violations.Should().Contain(v => v.Contains("found no dial", StringComparison.Ordinal));
    }

    private static DialplanCoverageScanner.Report Scan(FixtureTree tree) => DialplanCoverageScanner.Scan(tree.Root, NoUndefined, []);

    private static FixtureTree GreenTree(string body)
    {
        var tree = new FixtureTree();
        tree.Write(DialplanCoverageScanner.DialplanPath, GreenDialplan);
        tree.Write(DialplanCoverageScanner.ParkingPath, GreenParking);
        tree.Write(Test, "class SomeTests\n{\n    async Task M()\n    {\n" + body + "\n    }\n}\n");
        return tree;
    }

    private static string RepoRoot() => Path.GetDirectoryName(TestTreeSource.TestsRoot())!;

    private sealed class FixtureTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("dialplan-coverage-").FullName;

        public void Write(string relative, string text)
        {
            var path = Path.Join(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
