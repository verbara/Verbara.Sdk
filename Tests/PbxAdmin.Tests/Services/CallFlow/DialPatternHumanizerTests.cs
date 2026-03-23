using FluentAssertions;
using PbxAdmin.Services.CallFlow;

namespace PbxAdmin.Tests.Services.CallFlow;

public class DialPatternHumanizerTests
{
    // -----------------------------------------------------------------------
    // Describe
    // -----------------------------------------------------------------------

    [Fact]
    public void Describe_ShouldTranslate_NXXNXXXXXX()
    {
        DialPatternHumanizer.Describe("_NXXNXXXXXX").Should().Be("10-digit (e.g. 2125551234)");
    }

    [Fact]
    public void Describe_ShouldTranslate_1NXXNXXXXXX()
    {
        DialPatternHumanizer.Describe("_1NXXNXXXXXX").Should().Be("11-digit starting with 1");
    }

    [Fact]
    public void Describe_ShouldTranslate_NXXXXXX()
    {
        DialPatternHumanizer.Describe("_NXXXXXX").Should().Be("7-digit local");
    }

    [Fact]
    public void Describe_ShouldTranslate_00X()
    {
        DialPatternHumanizer.Describe("_00X.").Should().Be("International (00 prefix)");
    }

    [Fact]
    public void Describe_ShouldTranslate_011X()
    {
        DialPatternHumanizer.Describe("_011X.").Should().Be("International (011 prefix)");
    }

    [Fact]
    public void Describe_ShouldTranslate_911()
    {
        DialPatternHumanizer.Describe("911").Should().Be("Emergency 911");
    }

    [Fact]
    public void Describe_ShouldTranslate_N11()
    {
        DialPatternHumanizer.Describe("_N11").Should().Be("Service code (N11)");
    }

    [Fact]
    public void Describe_ShouldTranslate_CatchAll()
    {
        DialPatternHumanizer.Describe("_X.").Should().Be("Any number (catch-all)");
    }

    [Fact]
    public void Describe_ShouldTranslate_Exact()
    {
        DialPatternHumanizer.Describe("5551234567").Should().Be("Exact: 5551234567");
    }

    [Fact]
    public void Describe_ShouldShowRaw_Unknown()
    {
        DialPatternHumanizer.Describe("_[2-9]XX.").Should().Be("_[2-9]XX.");
    }

    // -----------------------------------------------------------------------
    // Example
    // -----------------------------------------------------------------------

    [Fact]
    public void Example_ShouldGenerate_NXXNXXXXXX()
    {
        var result = DialPatternHumanizer.Example("_NXXNXXXXXX");
        result.Should().NotBeNull();
        result.Should().HaveLength(10);
        result![0].Should().NotBe('0').And.NotBe('1');
    }

    [Fact]
    public void Example_ShouldReturn_ExactNumber()
    {
        DialPatternHumanizer.Example("5551234567").Should().Be("5551234567");
    }

    [Fact]
    public void Example_ShouldGenerate_CatchAll()
    {
        var result = DialPatternHumanizer.Example("_X.");
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Example_ShouldReturnNull_ForEmpty()
    {
        DialPatternHumanizer.Example("").Should().BeNull();
    }
}
