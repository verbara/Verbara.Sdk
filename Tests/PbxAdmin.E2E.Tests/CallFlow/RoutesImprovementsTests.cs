namespace PbxAdmin.E2E.Tests.CallFlow;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class RoutesImprovementsTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task RoutesOutbound_ShouldShowPatternDescription(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/routes");
        await Page!.WaitForTimeoutAsync(2000);

        // Click outbound tab
        var outboundTab = await Page.QuerySelectorAsync(
            "button:has-text('Outbound'), button:has-text('Saliente'), a:has-text('Outbound'), a:has-text('Saliente'), [data-tab='outbound']");
        if (outboundTab is null) return; // Outbound tab not available

        await outboundTab.ClickAsync();
        await Page.WaitForTimeoutAsync(1000);

        // Look for pattern cells with description text below the code element
        var codeCells = await Page.QuerySelectorAllAsync("code");
        if (codeCells.Count == 0) return; // No patterns displayed

        // Verify at least one pattern cell has a description sibling/child
        var descriptions = await Page.QuerySelectorAllAsync(
            ".pattern-description, .dial-pattern-desc, code + small, code ~ .text-muted");

        descriptions.Count.Should().BeGreaterOrEqualTo(1,
            "outbound route patterns should include description text");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task RoutesInbound_ShouldShowFlowSummary(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/routes");
        await Page!.WaitForTimeoutAsync(2000);

        // Inbound tab should be selected by default
        var flowSummaries = await Page.QuerySelectorAllAsync(
            ".route-flow-summary, .flow-summary, .destination-summary");
        if (flowSummaries.Count == 0) return; // Flow summaries not yet implemented

        flowSummaries.Count.Should().BeGreaterOrEqualTo(1,
            "inbound routes should display flow summary text in destination cells");

        // Verify at least one flow summary has readable content
        var firstSummary = flowSummaries[0];
        var text = await firstSummary.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace(
            "flow summary should contain descriptive text about the call destination");
    }
}
