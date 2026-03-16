using DashboardExample.Models;
using DashboardExample.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DashboardExample.Tests.Services;

public class ExtensionServiceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static IConfiguration BuildConfig(int rangeStart = 1000, int rangeEnd = 1999) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asterisk:Servers:0:Id"] = "pbx1",
                ["Asterisk:Servers:0:ExtensionRange:Start"] = rangeStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Asterisk:Servers:0:ExtensionRange:End"] = rangeEnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

    private static ExtensionService BuildService(
        IConfigProviderResolver resolver,
        IConfiguration? config = null)
    {
        config ??= BuildConfig();
        // AsteriskMonitorService is sealed — pass null; only test paths that don't reach it.
        return new ExtensionService(resolver, null!, config, NullLoggerFactory.Instance);
    }

    // -----------------------------------------------------------------------
    // CRUD tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateExtensionAsync_ShouldReject_WhenOutOfRange()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var service = BuildService(resolver); // range 1000-1999

        var config = new ExtensionConfig
        {
            Extension = "500", // outside 1000-1999
            Password = "password123",
            Codecs = "ulaw",
            Technology = ExtensionTechnology.PjSip,
        };

        var result = await service.CreateExtensionAsync("pbx1", config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CreateExtensionAsync_ShouldReject_WhenPasswordTooShort()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var provider = Substitute.For<IConfigProvider>();
        resolver.GetProvider("pbx1").Returns(provider);

        // No existing section → uniqueness passes
        provider.GetSectionAsync("pbx1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Dictionary<string, string>?)null);

        var service = BuildService(resolver); // range 1000-1999

        var config = new ExtensionConfig
        {
            Extension = "1100", // in range
            Password = "short",  // < 8 chars
            Codecs = "ulaw",
            Technology = ExtensionTechnology.PjSip,
        };

        var result = await service.CreateExtensionAsync("pbx1", config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CreateExtensionAsync_ShouldReject_WhenAlreadyExists()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var provider = Substitute.For<IConfigProvider>();
        resolver.GetProvider("pbx1").Returns(provider);

        // Simulate extension already existing in pjsip.conf
        provider.GetSectionAsync("pbx1", "pjsip.conf", "1200", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["type"] = "endpoint" });

        var service = BuildService(resolver); // range 1000-1999

        var config = new ExtensionConfig
        {
            Extension = "1200",
            Password = "password123",
            Codecs = "ulaw",
            Technology = ExtensionTechnology.PjSip,
        };

        var result = await service.CreateExtensionAsync("pbx1", config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CreateExtensionAsync_ShouldCreateEndpointAuthAor_ForPjsip()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var provider = Substitute.For<IConfigProvider>();
        resolver.GetProvider("pbx1").Returns(provider);

        // Extension does not exist
        provider.GetSectionAsync("pbx1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Dictionary<string, string>?)null);

        // All CreateSectionAsync calls succeed
        provider.CreateSectionAsync(
                "pbx1", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        provider.ReloadModuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = BuildService(resolver); // range 1000-1999

        var config = new ExtensionConfig
        {
            Extension = "1300",
            Password = "password123",
            Codecs = "ulaw",
            Technology = ExtensionTechnology.PjSip,
            VoicemailEnabled = false, // skip voicemail path
            DndEnabled = false,       // features == Empty → _features.SetAsync skipped
        };

        var result = await service.CreateExtensionAsync("pbx1", config);

        result.Should().BeTrue();

        // Verify 3 CreateSectionAsync calls: endpoint, auth, aor
        await provider.Received(3).CreateSectionAsync(
            "pbx1", "pjsip.conf", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateExtensionAsync_ShouldCreatePeer_ForSip()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var provider = Substitute.For<IConfigProvider>();
        resolver.GetProvider("pbx1").Returns(provider);

        // Extension does not exist in any file
        provider.GetSectionAsync("pbx1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Dictionary<string, string>?)null);

        provider.CreateSectionAsync(
                "pbx1", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        provider.ReloadModuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = BuildService(resolver); // range 1000-1999

        var config = new ExtensionConfig
        {
            Extension = "1400",
            Password = "password123",
            Codecs = "ulaw",
            Technology = ExtensionTechnology.Sip,
            VoicemailEnabled = false,
            DndEnabled = false,
        };

        var result = await service.CreateExtensionAsync("pbx1", config);

        result.Should().BeTrue();

        // Verify exactly 1 CreateSectionAsync call for sip.conf
        await provider.Received(1).CreateSectionAsync(
            "pbx1", "sip.conf", "1400",
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteExtensionAsync_ShouldRemoveAllSections()
    {
        var resolver = Substitute.For<IConfigProviderResolver>();
        var provider = Substitute.For<IConfigProvider>();
        resolver.GetProvider("pbx1").Returns(provider);

        provider.DeleteSectionAsync("pbx1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        provider.ReloadModuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var service = BuildService(resolver);

        // DeleteExtensionAsync calls CleanupAsync (monitor-dependent) inside try/catch — null monitor throws but is swallowed
        var result = await service.DeleteExtensionAsync("pbx1", "1500", ExtensionTechnology.PjSip);

        result.Should().BeTrue();

        // Verify 3 DeleteSectionAsync calls for PJSIP: endpoint, auth, aor
        await provider.Received(1).DeleteSectionAsync("pbx1", "pjsip.conf", "1500", Arg.Any<CancellationToken>());
        await provider.Received(1).DeleteSectionAsync("pbx1", "pjsip.conf", "1500-auth", Arg.Any<CancellationToken>());
        await provider.Received(1).DeleteSectionAsync("pbx1", "pjsip.conf", "1500-aor", Arg.Any<CancellationToken>());
    }


    [Fact]
    public void GetConfigFilename_ShouldReturnCorrectFile()
    {
        ExtensionService.GetConfigFilename(ExtensionTechnology.PjSip).Should().Be("pjsip.conf");
        ExtensionService.GetConfigFilename(ExtensionTechnology.Sip).Should().Be("sip.conf");
        ExtensionService.GetConfigFilename(ExtensionTechnology.Iax2).Should().Be("iax.conf");
    }

    [Fact]
    public void GetReloadModule_ShouldReturnCorrectModule()
    {
        ExtensionService.GetReloadModule(ExtensionTechnology.PjSip).Should().Be("res_pjsip.so");
        ExtensionService.GetReloadModule(ExtensionTechnology.Sip).Should().Be("chan_sip.so");
        ExtensionService.GetReloadModule(ExtensionTechnology.Iax2).Should().Be("chan_iax2.so");
    }

    [Fact]
    public void GetExtensionRange_ShouldReturnConfiguredRange()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asterisk:Servers:0:Id"] = "pbx1",
                ["Asterisk:Servers:0:ExtensionRange:Start"] = "200",
                ["Asterisk:Servers:0:ExtensionRange:End"] = "499",
            })
            .Build();

        var (start, end) = ExtensionService.GetExtensionRange(config, "pbx1");

        start.Should().Be(200);
        end.Should().Be(499);
    }

    [Fact]
    public void GetExtensionRange_ShouldReturnDefault_WhenNotConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asterisk:Servers:0:Id"] = "pbx1",
            })
            .Build();

        var (start, end) = ExtensionService.GetExtensionRange(config, "pbx1");

        start.Should().Be(100);
        end.Should().Be(999);
    }

    [Fact]
    public void GetExtensionRange_ShouldReturnDefault_WhenServerNotFound()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asterisk:Servers:0:Id"] = "pbx1",
            })
            .Build();

        var (start, end) = ExtensionService.GetExtensionRange(config, "nonexistent");

        start.Should().Be(100);
        end.Should().Be(999);
    }

    [Theory]
    [InlineData("100", 100, 999, true)]
    [InlineData("999", 100, 999, true)]
    [InlineData("500", 100, 999, true)]
    [InlineData("99", 100, 999, false)]
    [InlineData("1000", 100, 999, false)]
    [InlineData("abc", 100, 999, false)]
    [InlineData("", 100, 999, false)]
    public void IsInExtensionRange_ShouldReturnExpectedResult(string name, int rangeStart, int rangeEnd, bool expected)
    {
        ExtensionService.IsInExtensionRange(name, rangeStart, rangeEnd).Should().Be(expected);
    }

    [Fact]
    public void ExtractCallerIdName_ShouldParseQuotedName()
    {
        ExtensionService.ExtractCallerIdName("\"John Doe\" <100>").Should().Be("John Doe");
    }

    [Fact]
    public void ExtractCallerIdName_ShouldReturnNull_WhenNoQuotes()
    {
        ExtensionService.ExtractCallerIdName("100").Should().BeNull();
    }

    [Fact]
    public void ExtractCallerIdName_ShouldReturnNull_WhenNull()
    {
        ExtensionService.ExtractCallerIdName(null).Should().BeNull();
    }

    [Fact]
    public void ExtractIpFromContact_ShouldParseIp()
    {
        ExtensionService.ExtractIpFromContact("sip:100@192.168.1.10:5060").Should().Be("192.168.1.10");
    }

    [Fact]
    public void ExtractIpFromContact_ShouldReturnNull_WhenNull()
    {
        ExtensionService.ExtractIpFromContact(null).Should().BeNull();
    }

    [Fact]
    public void ExtractField_ShouldExtractValue()
    {
        var output = "Contact: sip:100@192.168.1.10\nUserAgent: Yealink T46U\n";
        ExtensionService.ExtractField(output, "UserAgent:").Should().Be("Yealink T46U");
    }

    [Fact]
    public void ExtractField_ShouldReturnNull_WhenNotFound()
    {
        ExtensionService.ExtractField("some output", "Missing:").Should().BeNull();
    }

    [Fact]
    public void ExtractRoundtrip_ShouldParseRtt()
    {
        var output = "  RTT: 15ms\n";
        ExtensionService.ExtractRoundtrip(output).Should().Be(15);
    }

    [Fact]
    public void ExtractRoundtrip_ShouldReturnNull_WhenNotFound()
    {
        ExtensionService.ExtractRoundtrip("no rtt here").Should().BeNull();
    }
}
