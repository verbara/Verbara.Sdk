using DashboardExample.Services.Helpers;
using FluentAssertions;

namespace DashboardExample.Tests.Services.Helpers;

public class DeviceFeatureHelperTests
{
    [Fact]
    public void ParseDatabaseShowOutput_ShouldExtractEntries()
    {
        // Realistic Asterisk CLI output from "database show CF"
        var output = """
            /CF/100                                           : 200
            /CF/101                                           : +15551234567
            /CF/200                                           : 300
            3 results found.
            """;

        var result = DeviceFeatureHelper.ParseDatabaseShowOutput(output);

        result.Should().HaveCount(3);
        result["100"].Should().Be("200");
        result["101"].Should().Be("+15551234567");
        result["200"].Should().Be("300");
    }

    [Fact]
    public void ParseDatabaseShowOutput_ShouldReturnEmpty_WhenNoResults()
    {
        var output = "0 results found.";

        var result = DeviceFeatureHelper.ParseDatabaseShowOutput(output);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseDatabaseShowOutput_ShouldReturnEmpty_WhenNull()
    {
        var result = DeviceFeatureHelper.ParseDatabaseShowOutput(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseDatabaseShowOutput_ShouldHandleSingleEntry()
    {
        var output = """
            /DND/100                                          : YES
            1 results found.
            """;

        var result = DeviceFeatureHelper.ParseDatabaseShowOutput(output);

        result.Should().HaveCount(1);
        result["100"].Should().Be("YES");
    }

    [Fact]
    public void ParseDatabaseShowOutput_ShouldIgnoreMalformedLines()
    {
        var output = """
            /CF/100                                           : 200
            some garbage line
            /CF/101                                           : 300
            2 results found.
            """;

        var result = DeviceFeatureHelper.ParseDatabaseShowOutput(output);

        result.Should().HaveCount(2);
        result["100"].Should().Be("200");
        result["101"].Should().Be("300");
    }

    [Fact]
    public void ParseDatabaseShowOutput_ShouldReturnEmpty_WhenEmptyString()
    {
        var result = DeviceFeatureHelper.ParseDatabaseShowOutput("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void DeviceFeatures_Empty_ShouldHaveDefaults()
    {
        var empty = DeviceFeatures.Empty;

        empty.Dnd.Should().BeFalse();
        empty.CfUnconditional.Should().BeNull();
        empty.CfBusy.Should().BeNull();
        empty.CfNoAnswer.Should().BeNull();
        empty.CfnaTimeout.Should().Be(20);
    }
}
