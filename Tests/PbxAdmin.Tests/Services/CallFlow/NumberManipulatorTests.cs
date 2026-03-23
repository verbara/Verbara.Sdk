using FluentAssertions;
using PbxAdmin.Services.CallFlow;

namespace PbxAdmin.Tests.Services.CallFlow;

public class NumberManipulatorTests
{
    // Apply tests

    [Fact]
    public void Apply_ShouldReturnUnchanged_WhenNoPrefixNoPrepend()
    {
        var result = NumberManipulator.Apply("18005551234", null, null);
        result.Should().Be("18005551234");
    }

    [Fact]
    public void Apply_ShouldPrepend_WhenPrependOnly()
    {
        var result = NumberManipulator.Apply("8005551234", null, "+1");
        result.Should().Be("+18005551234");
    }

    [Fact]
    public void Apply_ShouldStripPrefix_WhenPrefixOnly()
    {
        var result = NumberManipulator.Apply("98005551234", "9", null);
        result.Should().Be("8005551234");
    }

    [Fact]
    public void Apply_ShouldStripAndPrepend_WhenBoth()
    {
        // Strip "9" → "18005551234", prepend "+1" → "+118005551234"
        // For E.164 result "+18005551234", use prefix="91" instead
        var result = NumberManipulator.Apply("918005551234", "9", "+1");
        result.Should().Be("+118005551234");
    }

    [Fact]
    public void Apply_ShouldIgnoreEmptyStrings_WhenEmptyPrefixAndPrepend()
    {
        var result = NumberManipulator.Apply("5551234", "", "");
        result.Should().Be("5551234");
    }

    [Fact]
    public void Apply_ShouldNotStrip_WhenPrefixNotFound()
    {
        var result = NumberManipulator.Apply("5551234", "9", null);
        result.Should().Be("5551234");
    }

    // Preview tests

    [Fact]
    public void Preview_ShouldShowTransformation_WhenPrefixAndPrepend()
    {
        var result = NumberManipulator.Preview("9", "+1");
        result.Should().Be("9XXXXXXX → +1XXXXXXX");
    }

    [Fact]
    public void Preview_ShouldReturnEmpty_WhenNoPrefixNoPrepend()
    {
        var result = NumberManipulator.Preview(null, null);
        result.Should().Be("");
    }

    [Fact]
    public void Preview_ShouldShowPrependOnly_WhenPrependOnly()
    {
        var result = NumberManipulator.Preview(null, "+1");
        result.Should().Be("XXXXXXX → +1XXXXXXX");
    }

    [Fact]
    public void Preview_ShouldShowPrefixOnly_WhenPrefixOnly()
    {
        var result = NumberManipulator.Preview("9", null);
        result.Should().Be("9XXXXXXX → XXXXXXX");
    }
}
