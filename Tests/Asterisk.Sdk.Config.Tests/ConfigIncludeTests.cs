using Asterisk.Sdk.Config;
using FluentAssertions;

namespace Asterisk.Sdk.Config.Tests;

public class ConfigIncludeTests
{
    private static readonly string[] ExpectedHappyPathIncludes = ["b.conf", "c.conf"];


    private static string FixturePath(params string[] segments)
    {
        var baseDir = AppContext.BaseDirectory;
        var parts = new string[segments.Length + 2];
        parts[0] = baseDir;
        parts[1] = "Fixtures";
        Array.Copy(segments, 0, parts, 2, segments.Length);
        return Path.Combine(parts);
    }

    [Fact]
    public void ParseAsync_ShouldIncludeSectionsFromNestedFiles_WhenHappyPath()
    {
        var file = ConfigFileReader.Parse(FixturePath("Include", "happy-path", "a.conf"));

        file.GetCategory("general").Should().NotBeNull();
        file.GetCategory("middle").Should().NotBeNull();
        file.GetCategory("leaf").Should().NotBeNull();

        file.GetCategory("general")!.Variables["setting_a"].Should().Be("valueA");
        file.GetCategory("middle")!.Variables["setting_b"].Should().Be("valueB");
        file.GetCategory("leaf")!.Variables["setting_c"].Should().Be("valueC");
    }

    [Fact]
    public void ParseAsync_ShouldThrowConfigParseException_WhenCycleDetected()
    {
        var act = () => ConfigFileReader.Parse(FixturePath("Include", "cycle", "cycle-a.conf"));

        act.Should()
            .Throw<ConfigParseException>()
            .WithMessage("*cycle*");
    }

    [Fact]
    public void ParseAsync_ShouldContinueParsing_WhenTryIncludeMissing()
    {
        var file = ConfigFileReader.Parse(FixturePath("Include", "tryinclude", "main.conf"));

        file.GetCategory("main").Should().NotBeNull();
        file.GetCategory("after_include").Should().NotBeNull();
        file.GetCategory("main")!.Variables["setting_main"].Should().Be("value1");
        file.GetCategory("after_include")!.Variables["setting_after"].Should().Be("value2");

        // The directive itself is preserved on the file for diagnostics.
        file.Directives.Should().ContainSingle()
            .Which.Should().BeOfType<IncludeDirective>()
            .Which.IsTry.Should().BeTrue();
    }

    [Fact]
    public void ParseAsync_ShouldThrowConfigParseException_WhenIncludeMissing()
    {
        var act = () => ConfigFileReader.Parse(FixturePath("Include", "missing", "main.conf"));

        act.Should()
            .Throw<ConfigParseException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void ParseAsync_ShouldResolveRelativePaths_WhenNestedInSubdirectory()
    {
        var file = ConfigFileReader.Parse(FixturePath("Include", "relative", "outer.conf"));

        file.GetCategory("nested_section").Should().NotBeNull();
        file.GetCategory("nested_section")!.Variables["nested_setting"].Should().Be("yes");
    }

    [Fact]
    public void ParseAsync_ShouldSupportBothQuoteAndAngleBracketSyntax()
    {
        // happy-path uses "b.conf" (quoted) in a.conf and <c.conf> (angle) in b.conf.
        var file = ConfigFileReader.Parse(FixturePath("Include", "happy-path", "a.conf"));

        var includeDirectives = file.Directives.OfType<IncludeDirective>().ToList();
        includeDirectives.Should().HaveCount(2);
        includeDirectives.Select(d => d.Path).Should().BeEquivalentTo(ExpectedHappyPathIncludes);
        includeDirectives.Should().OnlyContain(d => !d.IsTry);
    }

}
