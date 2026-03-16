using DashboardExample.Models;
using DashboardExample.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DashboardExample.Tests.Services;

public class ExtensionServiceTests
{
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
