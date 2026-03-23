using System.Globalization;
using PbxAdmin.Models;
using PbxAdmin.Services;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class DialSimulatorTests
{
    private const string ServerId = "s1";

    // -----------------------------------------------------------------------
    // Pattern matching helper tests
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("_NXXXXXX", "5551234", true)]
    [InlineData("_NXXXXXX", "1234567", false)]    // starts with 1, N = [2-9]
    [InlineData("_0NXXXXXXXXX", "03001234567", true)]
    [InlineData("_0NXXXXXXXXX", "01234567890", false)] // second digit 1, N = [2-9]
    [InlineData("_3XXXXXXXXX", "3001234567", true)]
    [InlineData("_3XXXXXXXXX", "4001234567", false)]
    [InlineData("_00.", "0057312345678", true)]
    [InlineData("_00.", "00", false)]              // . requires one or more chars
    [InlineData("_+.", "+57312345678", true)]
    [InlineData("_911", "911", true)]
    [InlineData("_911", "9111", false)]
    [InlineData("_112", "112", true)]
    [InlineData("_900XXXXXXX", "9001234567", true)]
    [InlineData("_900XXXXXXX", "9011234567", false)]
    [InlineData("_Z.", "1234", true)]
    [InlineData("_Z.", "0234", false)]             // Z = [1-9]
    [InlineData("_[13]XX", "100", true)]
    [InlineData("_[13]XX", "300", true)]
    [InlineData("_[13]XX", "200", false)]
    public void MatchAsteriskPattern_ShouldMatchCorrectly(string pattern, string number, bool expected)
    {
        var result = DialSimulator.MatchAsteriskPattern(pattern, number);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("911", "911", true)]
    [InlineData("911", "9110", false)]
    [InlineData("*72", "*72", true)]
    [InlineData("#123", "#123", true)]
    public void MatchAsteriskPattern_ShouldMatchExact_WhenNoUnderscorePrefix(string pattern, string number, bool expected)
    {
        var result = DialSimulator.MatchAsteriskPattern(pattern, number);
        result.Should().Be(expected);
    }

    [Fact]
    public void MatchAsteriskPattern_ShouldHandleBangWildcard()
    {
        // ! = zero or more of anything
        DialSimulator.MatchAsteriskPattern("_9!", "9").Should().BeTrue();
        DialSimulator.MatchAsteriskPattern("_9!", "91234").Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Scenario 1: National COS dials local number -> ALLOWED
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldAllow_WhenNationalCosDials_LocalNumber()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupNationalCos(cosRepo, patternRepo);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 4);

        var result = await sut.SimulateAsync(ServerId, "2001", "5551234");

        result.Verdict.Should().Be(DialSimulatorVerdict.Allowed);
        result.MatchingPatternGroup.Should().Be("Local (Colombia)");
        result.ResolvedCosName.Should().Be("National");
        result.TraceLog.Should().NotBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Scenario 2: Internal COS dials national number -> DENIED
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldDeny_WhenInternalCosDials_NationalNumber()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupPatternGroups(patternRepo);

        // Internal COS: only emergency + local
        var internalLevel = new CosLevel
        {
            Id = 2, ServerId = ServerId, Name = "Internal", Priority = 2,
            AsteriskContext = "cos-internal",
            AllowPremium = false, AllowMobile = false,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 1, Action = "ALLOW", Sequence = 1, PatternGroupName = "Emergency" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
            ]
        };
        cosRepo.GetLevelAsync(2, Arg.Any<CancellationToken>()).Returns(internalLevel);
        SetupExtensionOverride(cosRepo, "2002", cosLevelId: 2);

        var result = await sut.SimulateAsync(ServerId, "2002", "03001234567");

        result.Verdict.Should().Be(DialSimulatorVerdict.Denied);
        result.ResolvedCosName.Should().Be("Internal");
    }

    // -----------------------------------------------------------------------
    // Scenario 3: National COS dials premium, AllowPremium=false -> DENIED
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldDeny_WhenPremiumFlagBlocked()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupNationalCos(cosRepo, patternRepo);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 4);

        var result = await sut.SimulateAsync(ServerId, "2001", "9001234567");

        result.Verdict.Should().Be(DialSimulatorVerdict.Denied);
        result.FlagChecks.Should().Contain(fc =>
            fc.FlagName == "AllowPremium" && !fc.FlagAllowed && fc.PatternMatches);
    }

    // -----------------------------------------------------------------------
    // Scenario 4: National COS dials mobile, AllowMobile=true -> ALLOWED
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldAllow_WhenMobileFlagAllowed()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupNationalCos(cosRepo, patternRepo);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 4);

        var result = await sut.SimulateAsync(ServerId, "2001", "3001234567");

        result.Verdict.Should().Be(DialSimulatorVerdict.Allowed);
        result.MatchingPatternGroup.Should().Be("Mobile (Colombia)");
        result.FlagChecks.Should().Contain(fc =>
            fc.FlagName == "AllowMobile" && fc.FlagAllowed && fc.PatternMatches);
    }

    // -----------------------------------------------------------------------
    // Scenario 5: Per-extension pattern override -> ALLOWED
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldAllow_WhenPerExtensionPatternOverrideMatches()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupPatternGroups(patternRepo);

        // Internal COS: only emergency + local (would deny international)
        var internalLevel = new CosLevel
        {
            Id = 2, ServerId = ServerId, Name = "Internal", Priority = 2,
            AsteriskContext = "cos-internal",
            AllowPremium = false, AllowMobile = false,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 1, Action = "ALLOW", Sequence = 1, PatternGroupName = "Emergency" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
            ]
        };
        cosRepo.GetLevelAsync(2, Arg.Any<CancellationToken>()).Returns(internalLevel);

        // Extension override with specific allowed pattern
        cosRepo.GetExtensionOverrideAsync(ServerId, "2003", Arg.Any<CancellationToken>())
            .Returns(new CosExtensionOverride
            {
                ServerId = ServerId, Extension = "2003", CosLevelId = 2,
                PatternOverrides = """[{"Pattern":"_0057.", "Action":"ALLOW"}]""",
            });
        cosRepo.GetTimeOverridesAsync(2, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await sut.SimulateAsync(ServerId, "2003", "005712345678");

        result.Verdict.Should().Be(DialSimulatorVerdict.Allowed);
        result.MatchingPattern.Should().Be("_0057.");
        result.TraceLog.Should().Contain(t => t.Contains("extension pattern override"));
    }

    // -----------------------------------------------------------------------
    // Scenario 6: No COS and no system default -> ERROR
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldReturnError_WhenNoCosAssigned()
    {
        var sut = CreateSimulator(out var cosRepo, out _);
        cosRepo.GetExtensionOverrideAsync(ServerId, "2099", Arg.Any<CancellationToken>())
            .Returns((CosExtensionOverride?)null);

        var result = await sut.SimulateAsync(ServerId, "2099", "5551234");

        result.Verdict.Should().Be(DialSimulatorVerdict.Error);
        result.ErrorMessage.Should().Contain("No COS assigned");
    }

    // -----------------------------------------------------------------------
    // Scenario 7: Time override switches COS
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldUseOverrideCos_WhenTimeWindowActive()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo, useTimeProvider: true);
        SetupPatternGroups(patternRepo);

        // National COS allows national calls
        var nationalLevel = new CosLevel
        {
            Id = 4, ServerId = ServerId, Name = "National", Priority = 4,
            AsteriskContext = "cos-national",
            AllowPremium = false, AllowMobile = true,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 1, Action = "ALLOW", Sequence = 1, PatternGroupName = "Emergency" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
                new CosLevelRule { PatternGroupId = 3, Action = "ALLOW", Sequence = 3, PatternGroupName = "National (Colombia)" },
            ]
        };

        // Internal COS: only emergency + local (would deny national)
        var internalLevel = new CosLevel
        {
            Id = 2, ServerId = ServerId, Name = "Internal", Priority = 2,
            AsteriskContext = "cos-internal",
            AllowPremium = false, AllowMobile = false,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 1, Action = "ALLOW", Sequence = 1, PatternGroupName = "Emergency" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
            ]
        };

        cosRepo.GetLevelAsync(4, Arg.Any<CancellationToken>()).Returns(nationalLevel);
        cosRepo.GetLevelAsync(2, Arg.Any<CancellationToken>()).Returns(internalLevel);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 4);

        // Time override: during current time window, use Internal instead
        var now = DateTimeOffset.UtcNow;
        var timeWindow = new CosTimeWindow
        {
            Id = 1, Name = "Night", ServerId = ServerId,
            DayOfWeek = (int)now.DayOfWeek,
            StartTime = TimeOnly.FromDateTime(now.DateTime).AddMinutes(-5),
            EndTime = TimeOnly.FromDateTime(now.DateTime).AddMinutes(5),
        };
        cosRepo.GetTimeOverridesAsync(4, Arg.Any<CancellationToken>())
            .Returns([new CosTimeOverride { Id = 1, CosLevelId = 4, OverrideCosLevelId = 2, TimeWindowId = 1, Priority = 100 }]);
        cosRepo.GetTimeWindowsAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns([timeWindow]);

        var result = await sut.SimulateAsync(ServerId, "2001", "03001234567");

        result.Verdict.Should().Be(DialSimulatorVerdict.Denied);
        result.TimeOverrideApplied.Should().Be("Night");
        result.ResolvedCosName.Should().Be("Internal");
    }

    // -----------------------------------------------------------------------
    // Additional edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SimulateAsync_ShouldDeny_WhenNoRuleMatches()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupPatternGroups(patternRepo);

        var emptyLevel = new CosLevel
        {
            Id = 1, ServerId = ServerId, Name = "Empty",
            AsteriskContext = "cos-empty",
            AllowPremium = false, AllowMobile = false,
            Rules = []
        };
        cosRepo.GetLevelAsync(1, Arg.Any<CancellationToken>()).Returns(emptyLevel);
        SetupExtensionOverride(cosRepo, "2099", cosLevelId: 1);

        var result = await sut.SimulateAsync(ServerId, "2099", "5551234");

        result.Verdict.Should().Be(DialSimulatorVerdict.Denied);
        result.TraceLog.Should().Contain(t => t.Contains("No rule matched"));
    }

    [Fact]
    public async Task SimulateAsync_ShouldUseDefaultCos_WhenNoExtensionOverride()
    {
        var config = BuildConfig(defaultCosLevelId: 4);
        var sut = CreateSimulator(out var cosRepo, out var patternRepo, config: config);
        SetupNationalCos(cosRepo, patternRepo);

        cosRepo.GetExtensionOverrideAsync(ServerId, "2001", Arg.Any<CancellationToken>())
            .Returns((CosExtensionOverride?)null);

        var result = await sut.SimulateAsync(ServerId, "2001", "5551234");

        result.Verdict.Should().Be(DialSimulatorVerdict.Allowed);
        result.ResolvedCosName.Should().Be("National");
        result.TraceLog.Should().Contain(t => t.Contains("default"));
    }

    [Fact]
    public async Task SimulateAsync_ShouldDenyWithExplicitDenyRule()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupPatternGroups(patternRepo);

        // COS with explicit DENY for national, ALLOW for local
        var restrictedLevel = new CosLevel
        {
            Id = 7, ServerId = ServerId, Name = "Restricted",
            AsteriskContext = "cos-restricted",
            AllowPremium = false, AllowMobile = false,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 3, Action = "DENY", Sequence = 1, PatternGroupName = "National (Colombia)" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
            ]
        };
        cosRepo.GetLevelAsync(7, Arg.Any<CancellationToken>()).Returns(restrictedLevel);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 7);

        var result = await sut.SimulateAsync(ServerId, "2001", "03001234567");

        result.Verdict.Should().Be(DialSimulatorVerdict.Denied);
        result.MatchingRuleName.Should().Be("National (Colombia)");
    }

    [Fact]
    public async Task SimulateAsync_ShouldPopulateTraceLog()
    {
        var sut = CreateSimulator(out var cosRepo, out var patternRepo);
        SetupNationalCos(cosRepo, patternRepo);
        SetupExtensionOverride(cosRepo, "2001", cosLevelId: 4);

        var result = await sut.SimulateAsync(ServerId, "2001", "5551234");

        result.TraceLog.Should().HaveCountGreaterThan(2);
        result.TraceLog[0].Should().Contain("Resolving COS");
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    private static DialSimulator CreateSimulator(
        out ICosRepository cosRepo,
        out ICosPatternGroupRepository patternRepo,
        IConfiguration? config = null,
        bool useTimeProvider = false)
    {
        cosRepo = Substitute.For<ICosRepository>();
        patternRepo = Substitute.For<ICosPatternGroupRepository>();

        var resolver = Substitute.For<ICosRepositoryResolver>();
        resolver.GetCosRepository(Arg.Any<string>()).Returns(cosRepo);
        resolver.GetPatternGroupRepository(Arg.Any<string>()).Returns(patternRepo);

        config ??= new ConfigurationBuilder().Build();

        // Default: no time overrides
        cosRepo.GetTimeOverridesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        cosRepo.GetTimeWindowsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return new DialSimulator(resolver, config);
    }

    private static void SetupPatternGroups(ICosPatternGroupRepository patternRepo)
    {
        var emergency = new CosPatternGroup { Id = 1, ServerId = ServerId, Name = "Emergency", Patterns = ["_911", "_112", "_999"] };
        var local = new CosPatternGroup { Id = 2, ServerId = ServerId, Name = "Local (Colombia)", Patterns = ["_NXXXXXX"] };
        var national = new CosPatternGroup { Id = 3, ServerId = ServerId, Name = "National (Colombia)", Patterns = ["_0NXXXXXXXXX"] };
        var international = new CosPatternGroup { Id = 4, ServerId = ServerId, Name = "International", Patterns = ["_00.", "_+."] };
        var premium = new CosPatternGroup { Id = 5, ServerId = ServerId, Name = "Premium (Colombia)", Patterns = ["_900XXXXXXX", "_901XXXXXXX"] };
        var mobile = new CosPatternGroup { Id = 6, ServerId = ServerId, Name = "Mobile (Colombia)", Patterns = ["_3XXXXXXXXX"] };

        patternRepo.GetGroupAsync(1, Arg.Any<CancellationToken>()).Returns(emergency);
        patternRepo.GetGroupAsync(2, Arg.Any<CancellationToken>()).Returns(local);
        patternRepo.GetGroupAsync(3, Arg.Any<CancellationToken>()).Returns(national);
        patternRepo.GetGroupAsync(4, Arg.Any<CancellationToken>()).Returns(international);
        patternRepo.GetGroupAsync(5, Arg.Any<CancellationToken>()).Returns(premium);
        patternRepo.GetGroupAsync(6, Arg.Any<CancellationToken>()).Returns(mobile);

        patternRepo.GetGroupsAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns([emergency, local, national, international, premium, mobile]);
    }

    private static void SetupNationalCos(ICosRepository cosRepo, ICosPatternGroupRepository patternRepo)
    {
        SetupPatternGroups(patternRepo);

        var nationalLevel = new CosLevel
        {
            Id = 4, ServerId = ServerId, Name = "National", Priority = 4,
            AsteriskContext = "cos-national",
            AllowPremium = false, AllowMobile = true,
            Rules =
            [
                new CosLevelRule { PatternGroupId = 1, Action = "ALLOW", Sequence = 1, PatternGroupName = "Emergency" },
                new CosLevelRule { PatternGroupId = 2, Action = "ALLOW", Sequence = 2, PatternGroupName = "Local (Colombia)" },
                new CosLevelRule { PatternGroupId = 3, Action = "ALLOW", Sequence = 3, PatternGroupName = "National (Colombia)" },
                new CosLevelRule { PatternGroupId = 5, Action = "ALLOW", Sequence = 4, PatternGroupName = "Premium (Colombia)" },
                new CosLevelRule { PatternGroupId = 6, Action = "ALLOW", Sequence = 5, PatternGroupName = "Mobile (Colombia)" },
            ]
        };
        cosRepo.GetLevelAsync(4, Arg.Any<CancellationToken>()).Returns(nationalLevel);
    }

    private static void SetupExtensionOverride(ICosRepository cosRepo, string extension, int cosLevelId)
    {
        cosRepo.GetExtensionOverrideAsync(ServerId, extension, Arg.Any<CancellationToken>())
            .Returns(new CosExtensionOverride
            {
                ServerId = ServerId, Extension = extension, CosLevelId = cosLevelId,
            });
    }

    private static IConfiguration BuildConfig(int defaultCosLevelId)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Asterisk:Servers:0:Id"] = ServerId,
            ["Asterisk:Servers:0:DefaultCosLevelId"] = defaultCosLevelId.ToString(CultureInfo.InvariantCulture),
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }
}
