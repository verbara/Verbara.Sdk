using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class CosDialplanGeneratorTests
{
    private const string ServerId = "s1";

    // -----------------------------------------------------------------------
    // Pattern contexts: simple (non-gated)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_ShouldProduceEmergencyContext()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Emergency", "_911", "_112")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        lines.Should().Contain(l => l.Context == "outbound-emergency" && l.Exten == "_911" && l.App == "Dial");
        lines.Should().Contain(l => l.Context == "outbound-emergency" && l.Exten == "_112" && l.App == "Dial");
    }

    [Fact]
    public async Task Generate_SimplePattern_ShouldDialAndHangup()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Local", "_2NXXXXXX")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var ctx = lines.Where(l => l.Context == "outbound-local").ToList();
        ctx.Should().Contain(l => l.Exten == "_2NXXXXXX" && l.Priority == 1 && l.App == "Dial"
            && l.AppData == "PJSIP/${EXTEN}@pstn-trunk,30");
        ctx.Should().Contain(l => l.Exten == "_2NXXXXXX" && l.Priority == 2 && l.App == "Hangup");
    }

    [Fact]
    public async Task Generate_ShouldProduceNationalContext()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("National", "_NXXNXXXXXX")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        lines.Should().Contain(l => l.Context == "outbound-national" && l.Exten == "_NXXNXXXXXX");
    }

    [Fact]
    public async Task Generate_ShouldProduceInternationalContext()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("International", "_011.")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        lines.Should().Contain(l => l.Context == "outbound-international" && l.Exten == "_011.");
    }

    // -----------------------------------------------------------------------
    // Pattern contexts: AstDB-gated (premium, mobile)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_ShouldGatePremiumWithAstDb()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Premium", "_900XXXXXXX")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var ctx = lines.Where(l => l.Context == "outbound-premium").ToList();
        ctx.Should().HaveCount(5);
        ctx.Should().Contain(l => l.App == "Set" && l.AppData.Contains("COS_PREMIUM") && l.AppData.Contains("CALLERID(num)"));
        ctx.Should().Contain(l => l.App == "GotoIf" && l.AppData.Contains("allow:block"));
        ctx.Should().Contain(l => l.App == "Dial" && l.AppData.Contains("PJSIP/${EXTEN}@pstn-trunk,30"));
        ctx.Should().Contain(l => l.App == "Playback" && l.AppData == "ss-noservice");
        ctx.Should().Contain(l => l.App == "Hangup");
    }

    [Fact]
    public async Task Generate_ShouldGateMobileWithAstDb()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Mobile", "_3XXXXXXXXX")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var ctx = lines.Where(l => l.Context == "outbound-mobile").ToList();
        ctx.Should().Contain(l => l.App == "Set" && l.AppData.Contains("COS_MOBILE"));
        ctx.Should().Contain(l => l.App == "GotoIf");
    }

    [Fact]
    public async Task Generate_GatedPattern_ShouldHaveCorrectPrioritySequence()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Premium", "_900XXXXXXX")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var ctx = lines.Where(l => l.Context == "outbound-premium").OrderBy(l => l.Priority).ToList();
        ctx[0].Priority.Should().Be(1);
        ctx[0].App.Should().Be("Set");
        ctx[1].Priority.Should().Be(2);
        ctx[1].App.Should().Be("GotoIf");
        ctx[2].Priority.Should().Be(3);
        ctx[2].App.Should().Be("Dial");
        ctx[3].Priority.Should().Be(4);
        ctx[3].App.Should().Be("Playback");
        ctx[4].Priority.Should().Be(5);
        ctx[4].App.Should().Be("Hangup");
    }

    // -----------------------------------------------------------------------
    // COS hierarchy: include chains
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_ShouldProduceCosHierarchyIncludes()
    {
        var sut = CreateGenerator(
            groups: [
                MakeGroup("Emergency", "_911"),
                MakeGroup("Local", "_2NXXXXXX"),
                MakeGroup("National", "_NXXNXXXXXX"),
            ],
            levels: [
                MakeLevel(1, "cos-emergency-only", 10, rules: [MakeRule(1, "Emergency")]),
                MakeLevel(2, "cos-local", 20, rules: [MakeRule(1, "Local")]),
                MakeLevel(3, "cos-national", 30, rules: [MakeRule(1, "National")]),
            ]);

        var lines = await sut.GenerateContextsAsync(ServerId);

        // cos-emergency-only has no lower level
        var emergencyIncludes = lines.Where(l => l.Context == "cos-emergency-only" && l.App == "include").ToList();
        emergencyIncludes.Should().ContainSingle(l => l.AppData == "outbound-emergency");
        emergencyIncludes.Should().NotContain(l => l.AppData.StartsWith("cos-"));

        // cos-local includes cos-emergency-only + outbound-local
        var localIncludes = lines.Where(l => l.Context == "cos-local" && l.App == "include").ToList();
        localIncludes.Should().Contain(l => l.AppData == "cos-emergency-only");
        localIncludes.Should().Contain(l => l.AppData == "outbound-local");

        // cos-national includes cos-local + outbound-national
        var nationalIncludes = lines.Where(l => l.Context == "cos-national" && l.App == "include").ToList();
        nationalIncludes.Should().Contain(l => l.AppData == "cos-local");
        nationalIncludes.Should().Contain(l => l.AppData == "outbound-national");
    }

    [Fact]
    public async Task Generate_CosHierarchy_ShouldSkipDisabledLevels()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Local", "_2NXXXXXX")],
            levels: [
                MakeLevel(1, "cos-emergency-only", 10, enabled: false),
                MakeLevel(2, "cos-local", 20, rules: [MakeRule(1, "Local")]),
            ]);

        var lines = await sut.GenerateContextsAsync(ServerId);

        // cos-local should not include disabled cos-emergency-only
        lines.Where(l => l.Context == "cos-local" && l.App == "include")
            .Should().NotContain(l => l.AppData == "cos-emergency-only");
    }

    [Fact]
    public async Task Generate_CosHierarchy_ShouldOnlyIncludeAllowedRules()
    {
        var sut = CreateGenerator(
            groups: [
                MakeGroup("Local", "_2NXXXXXX"),
                MakeGroup("Premium", "_900XXXXXXX"),
            ],
            levels: [
                MakeLevel(1, "cos-local", 10, rules: [
                    MakeRule(1, "Local", "ALLOW"),
                    MakeRule(2, "Premium", "DENY"),
                ]),
            ]);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var includes = lines.Where(l => l.Context == "cos-local" && l.App == "include").ToList();
        includes.Should().Contain(l => l.AppData == "outbound-local");
        includes.Should().NotContain(l => l.AppData == "outbound-premium");
    }

    [Fact]
    public async Task Generate_IncludeDirectives_ShouldUseCorrectConvention()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Local", "_2NXXXXXX")],
            levels: [MakeLevel(1, "cos-local", 10, rules: [MakeRule(1, "Local")])]);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var includes = lines.Where(l => l.App == "include").ToList();
        includes.Should().AllSatisfy(l =>
        {
            l.Exten.Should().Be(CosDialplanGenerator.IncludeExten);
            l.Priority.Should().Be(CosDialplanGenerator.IncludePriority);
        });
    }

    // -----------------------------------------------------------------------
    // Shared contexts
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_ShouldProduceLocalExtensionsContext()
    {
        var sut = CreateGenerator(groups: [], levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var local = lines.Where(l => l.Context == "local-extensions").ToList();
        local.Should().Contain(l => l.Exten == "_1XXX" && l.App == "Dial" && l.AppData.Contains("PJSIP/${EXTEN}"));
        local.Should().Contain(l => l.App == "Hangup");
    }

    [Fact]
    public async Task Generate_ShouldProduceServicesContext()
    {
        var sut = CreateGenerator(groups: [], levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var services = lines.Where(l => l.Context == "services").ToList();
        services.Should().Contain(l => l.Exten == "_*97" && l.App == "VoiceMailMain");
        services.Should().Contain(l => l.Exten == "_*98" && l.App == "VoiceMailMain");
        services.Should().Contain(l => l.Exten == "700" && l.App == "Park");
    }

    // -----------------------------------------------------------------------
    // DialplanData + DialplanGenerator integration
    // -----------------------------------------------------------------------

    [Fact]
    public void Generate_WithCosContexts_ShouldAppendToOutput()
    {
        var cosLines = new List<DialplanLine>
        {
            new("outbound-emergency", "_911", 1, "Dial", "PJSIP/${EXTEN}@pstn-trunk,30"),
            new("cos-local", "include", 0, "include", "outbound-local"),
        };

        var data = new DialplanData([], [], [], CosContexts: cosLines);
        var lines = DialplanGenerator.Generate(data);

        lines.Should().Contain(l => l.Context == "outbound-emergency" && l.Exten == "_911");
        lines.Should().Contain(l => l.Context == "cos-local" && l.App == "include");
    }

    [Fact]
    public void Generate_WithoutCosContexts_ShouldNotFail()
    {
        var data = new DialplanData([], [], []);
        var lines = DialplanGenerator.Generate(data);

        lines.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Multiple patterns per group
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_MultiplePatterns_ShouldCreateLineForEach()
    {
        var sut = CreateGenerator(
            groups: [MakeGroup("Emergency", "_911", "_112", "_999")],
            levels: []);

        var lines = await sut.GenerateContextsAsync(ServerId);

        var ctx = lines.Where(l => l.Context == "outbound-emergency").ToList();
        ctx.Where(l => l.App == "Dial").Should().HaveCount(3);
    }

    // -----------------------------------------------------------------------
    // Full hierarchy (7 built-in levels)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Generate_FullBuiltInHierarchy_ShouldChainCorrectly()
    {
        var sut = CreateGenerator(
            groups: [
                MakeGroup("Emergency", "_911", "_112"),
                MakeGroup("Local", "_2NXXXXXX"),
                MakeGroup("National", "_NXXNXXXXXX"),
                MakeGroup("Mobile", "_3XXXXXXXXX"),
                MakeGroup("Premium", "_900XXXXXXX"),
                MakeGroup("International", "_011."),
            ],
            levels: [
                MakeLevel(1, "cos-emergency-only", 10, rules: [MakeRule(1, "Emergency")]),
                MakeLevel(2, "cos-receive-only", 15, rules: []),
                MakeLevel(3, "cos-internal-only", 20, rules: []),
                MakeLevel(4, "cos-local", 30, rules: [MakeRule(1, "Local")]),
                MakeLevel(5, "cos-national", 40, rules: [MakeRule(1, "National"), MakeRule(2, "Mobile"), MakeRule(3, "Premium")]),
                MakeLevel(6, "cos-international", 50, rules: [MakeRule(1, "International")]),
                MakeLevel(7, "cos-unrestricted", 60, rules: []),
            ]);

        var lines = await sut.GenerateContextsAsync(ServerId);

        // Verify hierarchy chain
        GetIncludeTargets(lines, "cos-emergency-only").Should().Contain("outbound-emergency");
        GetIncludeTargets(lines, "cos-receive-only").Should().Contain("cos-emergency-only");
        GetIncludeTargets(lines, "cos-internal-only").Should().Contain("cos-receive-only");
        GetIncludeTargets(lines, "cos-local").Should().Contain("cos-internal-only");
        GetIncludeTargets(lines, "cos-local").Should().Contain("outbound-local");
        GetIncludeTargets(lines, "cos-national").Should().Contain("cos-local");
        GetIncludeTargets(lines, "cos-national").Should().Contain("outbound-national");
        GetIncludeTargets(lines, "cos-national").Should().Contain("outbound-mobile");
        GetIncludeTargets(lines, "cos-national").Should().Contain("outbound-premium");
        GetIncludeTargets(lines, "cos-international").Should().Contain("cos-national");
        GetIncludeTargets(lines, "cos-international").Should().Contain("outbound-international");
        GetIncludeTargets(lines, "cos-unrestricted").Should().Contain("cos-international");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<string> GetIncludeTargets(List<DialplanLine> lines, string context) =>
        lines.Where(l => l.Context == context && l.App == "include")
            .Select(l => l.AppData)
            .ToList();

    private static CosDialplanGenerator CreateGenerator(
        List<CosPatternGroup> groups, List<CosLevel> levels)
    {
        var cosRepo = Substitute.For<ICosRepository>();
        cosRepo.GetLevelsAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(levels));

        var patternRepo = Substitute.For<ICosPatternGroupRepository>();
        patternRepo.GetGroupsAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(groups));

        var resolver = Substitute.For<ICosRepositoryResolver>();
        resolver.GetCosRepository(ServerId).Returns(cosRepo);
        resolver.GetPatternGroupRepository(ServerId).Returns(patternRepo);

        return new CosDialplanGenerator(resolver);
    }

    private static CosPatternGroup MakeGroup(string name, params string[] patterns) =>
        new()
        {
            Id = Random.Shared.Next(1, 10000),
            ServerId = ServerId,
            Name = name,
            Patterns = patterns,
            IsBuiltIn = true,
        };

    private static CosLevel MakeLevel(int id, string asteriskContext, int priority,
        bool enabled = true, List<CosLevelRule>? rules = null) =>
        new()
        {
            Id = id,
            ServerId = ServerId,
            Name = asteriskContext.Replace("cos-", "").Replace("-", " "),
            AsteriskContext = asteriskContext,
            Priority = priority,
            IsBuiltIn = true,
            Enabled = enabled,
            Rules = rules ?? [],
        };

    private static CosLevelRule MakeRule(int sequence, string groupName, string action = "ALLOW") =>
        new()
        {
            Id = Random.Shared.Next(1, 10000),
            PatternGroupId = Random.Shared.Next(1, 10000),
            Action = action,
            Sequence = sequence,
            PatternGroupName = groupName,
        };
}
