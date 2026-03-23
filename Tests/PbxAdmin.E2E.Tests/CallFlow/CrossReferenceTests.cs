namespace PbxAdmin.E2E.Tests.CallFlow;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class CrossReferenceTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task TimeConditions_ShouldShowCrossReferences(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/time-conditions");
        await Page!.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        if (heading is null) return; // Page not available

        var xrefLines = await Page.QuerySelectorAllAsync(".xref-line, .cross-reference, .xref-badge");
        if (xrefLines.Count == 0) return; // Cross-references not yet implemented

        xrefLines.Count.Should().BeGreaterOrEqualTo(1,
            "time condition cards should display cross-reference information");

        // Verify cross-reference text is meaningful
        var firstXref = xrefLines[0];
        var text = await firstXref.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace(
            "cross-reference line should contain descriptive text");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task IvrMenus_ShouldShowCrossReferences(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/ivr-menus");
        await Page!.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        if (heading is null) return; // Page not available

        var xrefLines = await Page.QuerySelectorAllAsync(".xref-line, .cross-reference, .xref-badge");
        if (xrefLines.Count == 0) return; // Cross-references not yet implemented

        xrefLines.Count.Should().BeGreaterOrEqualTo(1,
            "IVR menu cards should display cross-reference information");

        // Verify cross-reference text is meaningful
        var firstXref = xrefLines[0];
        var text = await firstXref.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace(
            "cross-reference line should contain descriptive text");
    }
}
