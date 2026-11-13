using Xunit.Abstractions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The SDK's deprecation ids are one family held to <see cref="DeprecationIdRegister"/>: every mark's id is registered,
/// a marked id stays on its members with its link and its removal release, every registered guide keeps one heading per
/// id, a retired id is never used again, and a mark or suppression the scan cannot read fails rather than passes.
/// </summary>
/// <remarks>
/// The register cases read the real table; the real tree is read by <see cref="DeadModuleDeprecationGuardTests"/>. Each
/// other case builds a small fixture tree under a fixture register, so each failure the guard exists to catch is shown
/// to turn it red on its own. Marks and suppressions in the fixtures are text inside string literals, so the real scan
/// never reads one of them.
/// </remarks>
public sealed class DeprecationIdGuardTests(ITestOutputHelper output)
{
    private const string Retired = "VSDK0005";
    private const string Pkg = "Pkg";
    private const string Guide5 = "docs/guides/removal-2.9.md";
    private const string Guide10 = "docs/guides/migration-2.10.md";
    private const string Removal = "Removed in Verbara.Sdk 2.11.0.";

    private static readonly string Pragma = "#" + "pragma warning";

    private static readonly IReadOnlyList<DeprecationIdRow> Register =
    [
        new("VSDK0001", DeprecationIdState.Retired, "2.9.0", null, [], []),
        new("VSDK0002", DeprecationIdState.Marked, "2.11.0", Guide10, [new(Pkg, "Status.State"), new(Pkg, "Status.Link")], []),
        new("VSDK0004", DeprecationIdState.Marked, "2.11.0", Guide10, [new(Pkg, "Dtmf.DurationMs"), new(Pkg, "Hangup")], []),
        new(Retired, DeprecationIdState.Retired, "2.10.0", Guide5, [], []),
        new("VSDK0006", DeprecationIdState.Marked, "2.11.0", Guide10, [new(Pkg, "Metrics.Orphaned")], []),
    ];

    // ---- The register -------------------------------------------------------------------------------------------

    [Fact]
    public void Register_ShouldHoldTwoRetiredAndFiveMarkedIds_WhenReadOn2_10_0()
    {
        var rows = DeprecationIdRegister.Rows;

        rows.Select(r => r.Id).Should().Equal("VSDK0001", "VSDK0002", "VSDK0003", "VSDK0004", "VSDK0005", "VSDK0006", "VSDK0007");
        rows.Where(r => r.State == DeprecationIdState.Retired).Select(r => r.Id).Should().Equal("VSDK0001", "VSDK0005");
        rows.Where(r => r.State == DeprecationIdState.Marked).Select(r => (r.Id, r.Members.Count))
            .Should().Equal(("VSDK0002", 6), ("VSDK0003", 1), ("VSDK0004", 5), ("VSDK0006", 2), ("VSDK0007", 7));
        rows.Where(r => r.State == DeprecationIdState.Marked).Should().OnlyContain(r => r.Release == "2.11.0");
        rows.SelectMany(r => r.Members).Should().HaveCount(21).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Register_ShouldHoldTheTwentyOneMembersByPackage_WhenRead()
    {
        DeprecationIdRegister.Rows.SelectMany(r => r.Members.Select(m => $"{r.Id} {m.Package} {m.Key}")).Should().BeEquivalentTo(
        [
            "VSDK0002 Verbara.Sdk.Ami StatusEvent.CallerId", "VSDK0002 Verbara.Sdk.Ami StatusEvent.Account",
            "VSDK0002 Verbara.Sdk.Ami StatusEvent.State", "VSDK0002 Verbara.Sdk.Ami StatusEvent.BridgedChannel",
            "VSDK0002 Verbara.Sdk.Ami StatusEvent.Link", "VSDK0002 Verbara.Sdk.Ami StatusEvent.BridgedUniqueId",
            "VSDK0003 Verbara.Sdk.Ari AudioStreamMetrics.BufferUnderruns",
            "VSDK0004 Verbara.Sdk.Ari IChanWebSocketSession.SendXonAsync", "VSDK0004 Verbara.Sdk.Ari IChanWebSocketSession.SendXoffAsync",
            "VSDK0004 Verbara.Sdk.Ari ChanWebSocketHangup", "VSDK0004 Verbara.Sdk.Ari ChanWebSocketMediaBuffering",
            "VSDK0004 Verbara.Sdk.Ari ChanWebSocketDtmf.DurationMs",
            "VSDK0006 Verbara.Sdk.Sessions SessionMetrics.SessionsTimedOut", "VSDK0006 Verbara.Sdk.Sessions SessionMetrics.SessionsOrphaned",
            "VSDK0007 Verbara.Sdk.Ami DbPrefixGetAction", "VSDK0007 Verbara.Sdk.Ami ExecAction",
            "VSDK0007 Verbara.Sdk.Ami JitterBufStatsEvent", "VSDK0007 Verbara.Sdk.Ami MasqueradeEvent",
            "VSDK0007 Verbara.Sdk.Ami PriEventEvent", "VSDK0007 Verbara.Sdk.Ami RtpReceiverStatEvent",
            "VSDK0007 Verbara.Sdk.Ami RtpSenderStatEvent",
        ]);
    }

    [Theory]
    [InlineData("VSDK0002", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0002")]
    [InlineData("VSDK0003", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0003")]
    [InlineData("VSDK0004", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0004")]
    [InlineData("VSDK0005", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
    [InlineData("VSDK0006", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0006")]
    [InlineData("VSDK0007", "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
    public void Register_ShouldGiveEachIdWithAGuideTheLowerCaseLiteralLink_WhenRead(string id, string link)
    {
        DeprecationIdRegister.Row(id).Link.Should().Be(link).And.NotContain("{0}", "the compiler writes {0} as the id in upper case");
    }

    [Fact]
    public void Register_ShouldGiveVsdk0001NoGuide_WhenRead()
    {
        DeprecationIdRegister.Row("VSDK0001").Link.Should().BeNull("no symbol ever carried a link to a VSDK0001 heading");
    }

    // ---- A green fixture tree ---------------------------------------------------------------------------------------

    [Fact]
    public void Scan_ShouldPass_WhenEveryMarkIsRegisteredLinkedAndNamesItsRelease()
    {
        using var tree = GreenTree();

        var report = DeprecationIdScanner.Scan(tree.Root, Register);
        output.WriteLine(report.ToString());

        report.IsClean.Should().BeTrue(report.ToString());
        report.MarksFound.Should().Be(5);
    }

    // ---- What the scan cannot read (hole 1, audit F2, audit round 5) ------------------------------------------------

    public static TheoryData<string, string, int, string> UnreadableForms => new()
    {
        { "src/Pkg/ConstId.cs", "namespace Pkg;\n\npublic static class Ids { public const string Id = \"" + Retired + "\"; }\n[System.Obsolete(\"m\", DiagnosticId = Ids.Id)]\npublic sealed class A { }\n", 4, "gives its DiagnosticId as something other than a string literal" },
        { "Tests/Some/ConcatId.cs", "namespace Pkg;\n\n[System.Obsolete(\"m\", DiagnosticId = \"VSDK\" + \"0005\")]\npublic sealed class A { }\n", 3, "gives its DiagnosticId as something other than a string literal" },
        { "src/Pkg/InterpolatedId.cs", "namespace Pkg;\n\npublic static class Ids { public const string P = \"VSDK\"; }\n[System.Obsolete(\"m\", DiagnosticId = $\"{Ids.P}0005\")]\npublic sealed class A { }\n", 4, "gives its DiagnosticId as something other than a string literal" },
        { "src/Pkg/ConstUrl.cs", "namespace Pkg;\n\npublic static class Ids { public const string U = \"https://example.invalid/g#vsdk0005\"; }\n[System.Obsolete(\"m\", DiagnosticId = \"X0001\", UrlFormat = Ids.U)]\npublic sealed class A { }\n", 4, "gives its UrlFormat as something other than a string literal" },
        { "src/Pkg/ConcatUrl.cs", "namespace Pkg;\n\n[System.Obsolete(\"m\", DiagnosticId = \"X0001\", UrlFormat = \"https://example.invalid/g\" + \"#vsdk0005\")]\npublic sealed class A { }\n", 3, "gives its UrlFormat as something other than a string literal" },
        { "src/Pkg/AssemblyConst.cs", "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", Pkg.Ids.Retired)]\nnamespace Pkg;\npublic static class Ids { public const string Retired = \"" + Retired + "\"; }\n", 1, "SuppressMessage gives its check id as something other than a string literal" },
        { "src/Pkg/ModuleConst.cs", "[module: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", Pkg.Ids.Retired)]\nnamespace Pkg;\npublic static class Ids { public const string Retired = \"" + Retired + "\"; }\n", 1, "SuppressMessage gives its check id as something other than a string literal" },
        { "src/Pkg/AssemblyConcat.cs", "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"VSDK\" + \"0005\")]\n", 1, "SuppressMessage gives its check id as something other than a string literal" },
        { "src/Pkg/ModuleConcat.cs", "[module: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"VSDK\" + \"0005\")]\n", 1, "SuppressMessage gives its check id as something other than a string literal" },
        { "src/Pkg/LocalAlias.cs", "using Dep = System.ObsoleteAttribute;\n\nnamespace Pkg;\n\n[Dep(\"m\", DiagnosticId = \"" + Retired + "\")]\npublic sealed class A { }\n", 5, "[Dep] sets DiagnosticId under a name the guard does not read as [Obsolete]" },
        { "src/Pkg/GlobalAlias.cs", "global using Dep = System.ObsoleteAttribute;\n\nnamespace Pkg;\n\n[Dep(\"m\", DiagnosticId = \"" + Retired + "\")]\npublic sealed class A { }\n", 5, "[Dep] sets DiagnosticId under a name the guard does not read as [Obsolete]" },
        { "Tests/Some/GlobalSup.cs", "global using Sup = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;\n[assembly: Sup(\"Usage\", \"" + Retired + "\")]\n", 1, "a using alias names System.Diagnostics.CodeAnalysis.SuppressMessageAttribute" },
    };

    [Theory]
    [MemberData(nameof(UnreadableForms))]
    public void Scan_ShouldNameTheLine_WhenAMarkOrSuppressionIsWrittenInAFormTheGuardCannotRead(string path, string source, int line, string reason)
    {
        using var tree = GreenTree();
        tree.Write(path, source);

        var report = DeprecationIdScanner.Scan(tree.Root, Register);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{path}:{line}: ", StringComparison.Ordinal) && v.Contains(reason, StringComparison.Ordinal),
            "a guard that reads only literals would pass what it cannot read");
    }

    [Theory]
    [InlineData("using Dep = System.ObsoleteAttribute;\n", "System.ObsoleteAttribute")]
    [InlineData("global using Dep = global::System.ObsoleteAttribute;\n", "global::System.ObsoleteAttribute")]
    [InlineData("using Sup = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;\n", "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")]
    public void Scan_ShouldNameTheAlias_WhenAUsingAliasNamesAnAttributeTheGuardReads(string alias, string target)
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Alias.cs", alias + "\nnamespace Pkg;\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v.StartsWith($"src/Pkg/Alias.cs:1: a using alias names {target};", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldNameTheAttribute_WhenANamespaceAliasSpellsObsolete()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/NsAlias.cs", "using S = System;\n\nnamespace Pkg;\n\n[S.Obsolete(\"m\", DiagnosticId = \"" + Retired + "\")]\npublic sealed class A { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/NsAlias.cs:5: [S.Obsolete] sets DiagnosticId under a name the guard does not read as [Obsolete]; write [Obsolete] by its name");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnAliasNamesAnotherType()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/Alias.cs", "using Map = System.Collections.Generic.Dictionary<string, int>;\n\nnamespace Pkg;\n");

        DeprecationIdScanner.Scan(tree.Root, Register).IsClean.Should().BeTrue();
    }

    // ---- Unregistered ids and keying ----------------------------------------------------------------------------------

    [Fact]
    public void Scan_ShouldNameTheId_WhenAMarkCarriesAnIdTheRegisterDoesNotHold()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/New.cs", "namespace Pkg;\n\n" + Mark("VSDK0099", Removal, null) + "\npublic sealed class Fresh { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/New.cs:3: an [Obsolete] carries VSDK0099, which DeprecationIdRegister does not hold");
    }

    [Theory]
    [InlineData("namespace Pkg;\n\npublic sealed class C\n{\n    " + "__MARK__" + "\n    public C() { }\n}\n", 5)]
    [InlineData("namespace Pkg;\n\npublic enum E\n{\n    " + "__MARK__" + "\n    One,\n}\n", 5)]
    [InlineData("namespace Pkg;\n\npublic sealed class C\n{\n    " + "__MARK__" + "\n    public int this[int i] => i;\n}\n", 5)]
    [InlineData("namespace Pkg;\n\npublic sealed class C\n{\n    public void M(" + "__MARK__" + " int x) { }\n}\n", 5)]
    public void Scan_ShouldFail_WhenAVsdkMarkSitsOnATargetItCannotKey(string template, int line)
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Unkeyed.cs", template.Replace("__MARK__", Mark("VSDK0002", Removal, Link("VSDK0002")), StringComparison.Ordinal));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v == $"src/Pkg/Unkeyed.cs:{line}: an [Obsolete] carrying VSDK0002 sits on a target the guard cannot key to Type or Type.Member");
    }

    [Fact]
    public void Scan_ShouldKeyARecordPositionalParameter_WhenItIsMarkedThroughProperty()
    {
        var register = Register.Select(r => r.Id == "VSDK0004" ? r with { Members = [new(Pkg, "Hangup")] } : r).ToList();
        using var tree = GreenTree();

        var report = DeprecationIdScanner.Scan(tree.Root, register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Dtmf.cs:5: Dtmf.DurationMs carries VSDK0004 but is outside its registered set");
    }

    // ---- Member sets --------------------------------------------------------------------------------------------------

    [Fact]
    public void Scan_ShouldNameTheMember_WhenAMarkedIdSpreadsOutsideItsSet()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Status.cs", StatusSource(extra: true));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v.StartsWith("src/Pkg/Status.cs:", StringComparison.Ordinal)
            && v.EndsWith(": Status.Seventh carries VSDK0002 but is outside its registered set", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnotherPackageHoldsAMemberOfTheSameNameUnmarked()
    {
        using var tree = GreenTree();
        tree.Write("src/Other/Metrics.cs", "namespace Other;\n\npublic static class Metrics\n{\n    public static readonly int Orphaned = 0;\n}\n");

        DeprecationIdScanner.Scan(tree.Root, Register).IsClean.Should().BeTrue();
    }

    [Fact]
    public void Scan_ShouldNameTheMember_WhenARegisteredMemberLosesItsMark()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs", "namespace Pkg;\n\npublic static class Metrics\n{\n    public static readonly int Orphaned = 0;\n}\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg: Metrics.Orphaned is registered under VSDK0006 but carries no VSDK0006 mark");
    }

    [Fact]
    public void Scan_ShouldNameEveryMissingMember_WhenTheTreeHoldsNoneOfTheRegisteredMarks()
    {
        using var tree = new FixtureTree();
        tree.Write("src/Pkg/Kept.cs", "namespace Pkg;\n\npublic sealed class KeptAction { }\n");
        WriteGuides(tree);

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().Contain("found none of the 5 marks the register lists: the guard would pass on nothing");
        report.Violations.Count(v => v.Contains(" is registered under ", StringComparison.Ordinal)).Should().Be(5);
    }

    [Fact]
    public void Scan_ShouldFail_WhenNoSourceFileIsFoundUnderSrc()
    {
        using var tree = new FixtureTree();
        WriteGuides(tree);

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().Contain(v => v.StartsWith("found no source file under src/", StringComparison.Ordinal));
    }

    // ---- Links --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Scan_ShouldNameTheMemberAndTheExpectedLink_WhenAMarkHasNoLink()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs", MetricsSource(url: null));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == $"src/Pkg/Metrics.cs:5: Metrics.Orphaned carries VSDK0006 with no UrlFormat; the registered link is {Link("VSDK0006")}");
    }

    [Theory]
    [InlineData("https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.md#{0}", true)]
    [InlineData("https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.md#VSDK0006", false)]
    [InlineData("https://github.com/verbara/Verbara.Sdk/blob/v2.10.0/docs/guides/migration-2.10.md#vsdk0006", false)]
    public void Scan_ShouldNameTheLink_WhenAMarkCarriesAnotherOne(string url, bool placeholder)
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs", MetricsSource(url));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v.StartsWith($"src/Pkg/Metrics.cs:5: Metrics.Orphaned carries VSDK0006 with UrlFormat \"{url}\", not the registered link {Link("VSDK0006")}", StringComparison.Ordinal)
            && v.Contains("upper case", StringComparison.Ordinal) == placeholder);
    }

    // ---- Messages -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"Not emitted.\"", "with a message that names no removal release; the register expects \"Removed in Verbara.Sdk 2.11.0.\"")]
    [InlineData("\"Not emitted. Removed in Verbara.Sdk 2.12.0.\"", "with a message naming removal in 2.12.0; the register expects \"Removed in Verbara.Sdk 2.11.0.\"")]
    [InlineData("\"Not emitted since Verbara.Sdk 2.6.0. Removed in Verbara.Sdk 2.11.0.\"", "with a message that names a second SDK release; it must name only \"Removed in Verbara.Sdk 2.11.0.\"")]
    [InlineData("Msg.Text", "with a message that is not a string literal; it must name \"Removed in Verbara.Sdk 2.11.0.\"")]
    public void Scan_ShouldNameTheMemberAndTheRelease_WhenAMessageDoesNotNameTheRegisteredRemoval(string message, string reason)
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs", MetricsSource(Link("VSDK0006"), message));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == $"src/Pkg/Metrics.cs:5: Metrics.Orphaned carries VSDK0006 {reason}");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAMessageNamesAnAsteriskVersionBesideItsRemovalRelease()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs", MetricsSource(Link("VSDK0006"), "\"Measured on 22.10.1 and 24.0.0-rc2. " + Removal + "\""));

        DeprecationIdScanner.Scan(tree.Root, Register).IsClean.Should().BeTrue();
    }

    [Fact]
    public void Scan_ShouldNameTheRelease_WhenAMarkHasNoMessage()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Metrics.cs",
            "namespace Pkg;\n\npublic static class Metrics\n{\n    [System.Obsolete(DiagnosticId = \"VSDK0006\", UrlFormat = \"" + Link("VSDK0006") + "\")]\n    public static readonly int Orphaned = 0;\n}\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Metrics.cs:5: Metrics.Orphaned carries VSDK0006 with no message; it must name \"Removed in Verbara.Sdk 2.11.0.\"");
    }

    // ---- Headings -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("# Guide\n\n#### VSDK0002\n\n#### VSDK0004\n", 0)]
    [InlineData("# Guide\n\n#### VSDK0002\n\n#### VSDK0004\n\n#### VSDK0006\n\n## VSDK0006\n", 2)]
    [InlineData("# Guide\n\n#### VSDK0002\n\n#### VSDK0004\n\n```md\n#### VSDK0006\n```\n", 0)]
    public void Scan_ShouldNameTheGuideAndTheId_WhenAMarkedIdDoesNotHaveExactlyOneHeading(string guide, int headings)
    {
        using var tree = GreenTree();
        tree.Write(Guide10, guide);

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle().Which.Should().Be($"{Guide10}: holds {headings} headings whose text is \"VSDK0006\", not exactly one");
    }

    [Fact]
    public void Scan_ShouldNameTheGuideAndTheId_WhenARetiredIdLosesItsHeading()
    {
        using var tree = GreenTree();
        tree.Write(Guide5, "# Guide\n\n## Removed\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle().Which.Should().Be($"{Guide5}: holds 0 headings whose text is \"{Retired}\", not exactly one");
    }

    [Fact]
    public void Scan_ShouldNameEachId_WhenTheirGuideIsMissing()
    {
        using var tree = GreenTree();
        File.Delete(Path.Join(tree.Root, Guide10));

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().BeEquivalentTo(
            $"{Guide10}: missing; the VSDK0002 link points into it",
            $"{Guide10}: missing; the VSDK0004 link points into it",
            $"{Guide10}: missing; the VSDK0006 link points into it");
    }

    // ---- Retired ids ------------------------------------------------------------------------------------------------

    [Fact]
    public void Scan_ShouldNameTheRetiredId_WhenVsdk0001IsBroughtBackAsAMark()
    {
        using var tree = GreenTree();
        tree.Write("src/Pkg/Back.cs", "namespace Pkg;\n\n" + Mark("VSDK0001", Removal, null) + "\npublic sealed class Back { }\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "src/Pkg/Back.cs:3: an [Obsolete] carries the retired id VSDK0001");
    }

    [Fact]
    public void Scan_ShouldNameTheFile_WhenBuildConfigurationKeepsVsdk0001()
    {
        using var tree = GreenTree();
        tree.Write("Directory.Build.props", "<Project><PropertyGroup>\n<NoWarn>$(NoWarn);VSDK0001</NoWarn></PropertyGroup></Project>\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "Directory.Build.props:2: build configuration names the retired id VSDK0001");
    }

    [Fact]
    public void Scan_ShouldNameTheLine_WhenAPragmaNamesVsdk0001()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/Site.cs", "namespace Pkg;\n\n" + Pragma + " disable VSDK0001\n");

        var report = DeprecationIdScanner.Scan(tree.Root, Register);

        report.Violations.Should().ContainSingle(v => v == "Tests/Some/Site.cs:3: a #pragma warning names the retired id VSDK0001");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAPragmaNamesAMarkedId()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/Site.cs", "namespace Pkg;\n\n" + Pragma + " disable VSDK0002 // the obsolete read is the point\n");

        DeprecationIdScanner.Scan(tree.Root, Register).IsClean.Should().BeTrue();
    }

    // ---- Fixtures -----------------------------------------------------------------------------------------------------

    private static string Link(string id) =>
        DeprecationIdRegister.RepositoryBlob + Guide10 + "#" + DeprecationIdScanner.Slug(id);

    private static string Mark(string id, string message, string? url, string target = "") =>
        "[" + target + "System.Obsolete(\"" + message + "\", DiagnosticId = \"" + id + "\"" + (url is null ? "" : ", UrlFormat = \"" + url + "\"") + ")]";

    private static string StatusSource(bool extra) =>
        "namespace Pkg;\n\npublic sealed class Status\n{\n" +
        "    " + Mark("VSDK0002", Removal, Link("VSDK0002")) + "\n    public string? State { get; set; }\n" +
        "    " + Mark("VSDK0002", Removal, Link("VSDK0002")) + "\n    public string? Link { get; set; }\n" +
        (extra ? "    " + Mark("VSDK0002", Removal, Link("VSDK0002")) + "\n    public string? Seventh { get; set; }\n" : "") +
        "    public string? Kept { get; set; }\n}\n";

    private static string MetricsSource(string? url, string? message = null) =>
        "namespace Pkg;\n\npublic static class Metrics\n{\n    [System.Obsolete(" + (message ?? "\"Not emitted. " + Removal + "\"") +
        ", DiagnosticId = \"VSDK0006\"" + (url is null ? "" : ", UrlFormat = \"" + url + "\"") + ")]\n    public static readonly int Orphaned = 0;\n}\n";

    private static void WriteGuides(FixtureTree tree)
    {
        tree.Write(Guide5, "# Guide\n\n## " + Retired + "\n");
        tree.Write(Guide10, "# Guide\n\n### Removal\n\n#### VSDK0002\n\n#### VSDK0004\n\n#### VSDK0006\n");
    }

    private static FixtureTree GreenTree()
    {
        var tree = new FixtureTree();
        tree.Write("src/Pkg/Status.cs", StatusSource(extra: false));
        tree.Write("src/Pkg/Dtmf.cs",
            "namespace Pkg;\n\npublic sealed record Dtmf(\n    string Digit,\n    " + Mark("VSDK0004", "Always 0. " + Removal, Link("VSDK0004"), "property: ") + "\n    int DurationMs);\n\n" +
            Mark("VSDK0004", "Never published. " + Removal, Link("VSDK0004")) + "\npublic sealed record Hangup(string? Cause);\n");
        tree.Write("src/Pkg/Metrics.cs", MetricsSource(Link("VSDK0006")));
        tree.Write("src/Pkg/Pkg.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        WriteGuides(tree);
        return tree;
    }

    private sealed class FixtureTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("deprecation-id-").FullName;

        public void Write(string relative, string text)
        {
            var path = Path.Join(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
