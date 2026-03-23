using FluentAssertions;
using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;

namespace PbxAdmin.Tests.Services.Dialplan;

public class DialplanEditorServiceTests
{
    // ── Circular include detection ──

    [Fact]
    public void HasCircularInclude_ShouldReturnTrue_WhenDirectCycle()
    {
        // A includes B, adding B->A would create A->B->A
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B"] },
            new() { Name = "B", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "B", "A")
            .Should().BeTrue();
    }

    [Fact]
    public void HasCircularInclude_ShouldReturnTrue_WhenIndirectCycle()
    {
        // A->B->C, adding C->A would create A->B->C->A
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B"] },
            new() { Name = "B", Includes = ["C"] },
            new() { Name = "C", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "C", "A")
            .Should().BeTrue();
    }

    [Fact]
    public void HasCircularInclude_ShouldReturnFalse_WhenNoCycle()
    {
        // A->B, C->B — adding C->D creates no cycle
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B"] },
            new() { Name = "B", Includes = [] },
            new() { Name = "C", Includes = ["B"] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "C", "D")
            .Should().BeFalse();
    }

    [Fact]
    public void HasCircularInclude_ShouldReturnFalse_WhenDisconnected()
    {
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = [] },
            new() { Name = "B", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "A", "B")
            .Should().BeFalse();
    }

    [Fact]
    public void HasCircularInclude_ShouldReturnTrue_WhenSelfReference()
    {
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "A", "A")
            .Should().BeTrue();
    }

    [Fact]
    public void HasCircularInclude_ShouldReturnFalse_WhenParentNotInSnapshot()
    {
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B"] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "unknown", "A")
            .Should().BeFalse();
    }

    // ── AMI command builders ──

    [Fact]
    public void BuildAddExtensionCommand_ShouldFormatCorrectly()
    {
        var cmd = DialplanEditorService.BuildAddExtensionCommand(
            "default", "100", 1, "Dial", "PJSIP/100,30");

        cmd.Should().Be("dialplan add extension 100,1,Dial(PJSIP/100,30) into default");
    }

    [Fact]
    public void BuildAddExtensionCommand_ShouldHandleEmptyAppData()
    {
        var cmd = DialplanEditorService.BuildAddExtensionCommand(
            "default", "100", 1, "Answer", "");

        cmd.Should().Be("dialplan add extension 100,1,Answer() into default");
    }

    [Fact]
    public void BuildRemoveExtensionCommand_ShouldFormatCorrectly()
    {
        var cmd = DialplanEditorService.BuildRemoveExtensionCommand("default", "100");

        cmd.Should().Be("dialplan remove extension 100@default");
    }

    [Fact]
    public void BuildAddIncludeCommand_ShouldFormatCorrectly()
    {
        var cmd = DialplanEditorService.BuildAddIncludeCommand("default", "outbound");

        cmd.Should().Be("dialplan add include outbound into default");
    }

    [Fact]
    public void BuildRemoveIncludeCommand_ShouldFormatCorrectly()
    {
        var cmd = DialplanEditorService.BuildRemoveIncludeCommand("default", "outbound");

        cmd.Should().Be("dialplan remove include outbound from default");
    }

    [Fact]
    public void BuildRemoveContextCommand_ShouldFormatCorrectly()
    {
        var cmd = DialplanEditorService.BuildRemoveContextCommand("my-context");

        cmd.Should().Be("dialplan remove context my-context");
    }

    // ── CreateContext delegates to AddExtension with NoOp placeholder ──

    [Fact]
    public void BuildCreateContextCommands_ShouldUseNoOpPlaceholder()
    {
        // CreateContext internally calls AddExtension with s,1,NoOp(placeholder)
        var cmd = DialplanEditorService.BuildAddExtensionCommand(
            "new-context", "s", 1, "NoOp", "placeholder");

        cmd.Should().Be("dialplan add extension s,1,NoOp(placeholder) into new-context");
    }

    // ── Deep cycle detection (4 levels) ──

    [Fact]
    public void HasCircularInclude_ShouldReturnTrue_WhenDeepCycle()
    {
        // A->B->C->D, adding D->A creates cycle
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B"] },
            new() { Name = "B", Includes = ["C"] },
            new() { Name = "C", Includes = ["D"] },
            new() { Name = "D", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "D", "A")
            .Should().BeTrue();
    }

    [Fact]
    public void HasCircularInclude_ShouldHandleDiamondGraph_WithoutFalsePositive()
    {
        // A->B, A->C, B->D, C->D — adding D->E is fine (diamond, no cycle)
        var contexts = new List<DiscoveredContext>
        {
            new() { Name = "A", Includes = ["B", "C"] },
            new() { Name = "B", Includes = ["D"] },
            new() { Name = "C", Includes = ["D"] },
            new() { Name = "D", Includes = [] },
        };

        DialplanEditorService.HasCircularInclude(contexts, "D", "E")
            .Should().BeFalse();
    }
}
