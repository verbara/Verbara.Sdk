using DashboardExample.Models;
using DashboardExample.Services.Dialplan;
using FluentAssertions;

namespace DashboardExample.Tests.Services;

public class IvrDialplanGeneratorTests
{
    [Fact]
    public void Generate_ShouldCreateContext()
    {
        var menu = ValidMenu();
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "s" && l.App == "Answer");
    }

    [Fact]
    public void Generate_ShouldIncludeBackground()
    {
        var menu = ValidMenu() with { Greeting = "welcome" };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.App == "Background" && l.AppData == "welcome");
    }

    [Fact]
    public void Generate_ShouldIncludeWaitExten()
    {
        var menu = ValidMenu() with { Timeout = 7 };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.App == "WaitExten" && l.AppData == "7");
    }

    [Fact]
    public void Generate_ShouldMapExtensionDest()
    {
        var menu = ValidMenu() with { Items = [Item("1", "extension", "1001")] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "1" && l.App == "Goto" && l.AppData == "from-internal,1001,1");
    }

    [Fact]
    public void Generate_ShouldMapQueueDest()
    {
        var menu = ValidMenu() with { Items = [Item("2", "queue", "sales")] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "2" && l.App == "Goto" && l.AppData == "queues,sales,1");
    }

    [Fact]
    public void Generate_ShouldMapIvrDest()
    {
        var menu = ValidMenu() with { Items = [Item("3", "ivr", "sub-menu")] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "3" && l.App == "Goto" && l.AppData == "ivr-sub-menu,s,1");
    }

    [Fact]
    public void Generate_ShouldMapVoicemailDest()
    {
        var menu = ValidMenu() with { Items = [Item("9", "voicemail", "1000")] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "9" && l.App == "VoiceMail" && l.AppData == "1000@default,u");
    }

    [Fact]
    public void Generate_ShouldMapHangupDest()
    {
        var menu = ValidMenu() with { Items = [Item("*", "hangup", "")] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "*" && l.App == "Hangup");
    }

    [Fact]
    public void Generate_ShouldMapExternalDest()
    {
        var menu = ValidMenu() with { Items = [new IvrMenuItemConfig { Digit = "8", DestType = "external", DestTarget = "5551234", Trunk = "trunk-1" }] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "8" && l.App == "Dial" && l.AppData.Contains("PJSIP/5551234@trunk-1"));
    }

    [Fact]
    public void Generate_ShouldMapExternalDest_WhenTrunkNull()
    {
        var menu = ValidMenu() with { Items = [new IvrMenuItemConfig { Digit = "8", DestType = "external", DestTarget = "5551234", Trunk = null }] };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "8" && l.App == "Dial" && l.AppData.Contains("PJSIP/5551234"));
    }

    [Fact]
    public void Generate_ShouldIncludeRetryLoop()
    {
        var menu = ValidMenu() with { MaxRetries = 3 };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "t" && l.App == "GotoIf" && l.AppData.Contains("<3") && l.AppData.Contains("s,2"));
    }

    [Fact]
    public void Generate_ShouldIncludeInvalidHandler()
    {
        var menu = ValidMenu();
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().Contain(l => l.Context == "ivr-main" && l.Exten == "i" && l.App == "Playback" && l.AppData == "option-is-invalid");
    }

    [Fact]
    public void Generate_ShouldSkipDisabledMenu()
    {
        var menu = ValidMenu() with { Enabled = false };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().NotContain(l => l.Context == "ivr-main");
    }

    [Fact]
    public void Generate_ShouldHandleNoGreeting()
    {
        var menu = ValidMenu() with { Greeting = null };
        var data = DataWith(menu);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().NotContain(l => l.Context == "ivr-main" && l.App == "Background");
        lines.Should().Contain(l => l.Context == "ivr-main" && l.App == "WaitExten");
    }

    [Fact]
    public void ResolveDestination_ShouldHandleIvrType()
    {
        var result = DialplanGenerator.ResolveDestination("ivr", "main-menu");
        result.Should().Be("ivr-main-menu,s,1");
    }

    // ─── Helpers ───

    private static IvrMenuConfig ValidMenu() => new()
    {
        Id = 1, ServerId = "srv1", Name = "main", Label = "Main Menu",
        Greeting = "welcome", Timeout = 5, MaxRetries = 3, Enabled = true,
        Items = [Item("1", "extension", "1001"), Item("2", "queue", "sales")]
    };

    private static IvrMenuItemConfig Item(string digit, string destType, string destTarget) => new()
    {
        Digit = digit, DestType = destType, DestTarget = destTarget
    };

    private static DialplanData DataWith(IvrMenuConfig menu) => new([], [], [], [menu]);
}
