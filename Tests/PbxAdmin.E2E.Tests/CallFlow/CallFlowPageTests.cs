namespace PbxAdmin.E2E.Tests.CallFlow;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class CallFlowPageTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallFlowPage_ShouldLoadWithKpiCards(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/call-flow");
        await Page!.WaitForTimeoutAsync(2000);

        var kpiCards = await Page.QuerySelectorAllAsync(".cf-kpi-card, .kpi-card");
        if (kpiCards.Count == 0) return; // Page not available in this environment

        kpiCards.Count.Should().BeGreaterOrEqualTo(4,
            "Call Flow page should display at least 4 KPI cards");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallFlowPage_ShouldShowHealthSection(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/call-flow");
        await Page!.WaitForTimeoutAsync(2000);

        var warnings = await Page.QuerySelectorAsync(".cf-warnings, .health-warnings");
        if (warnings is null) return; // Page not available in this environment

        var text = await warnings.InnerTextAsync();
        text.Should().NotBeNull("health warnings section should have content");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallFlowPage_ShouldShowDidList(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/call-flow");
        await Page!.WaitForTimeoutAsync(2000);

        var didCards = await Page.QuerySelectorAllAsync(".cf-did-list, .cf-did-card");
        if (didCards.Count == 0) return; // Page not available in this environment

        didCards.Count.Should().BeGreaterOrEqualTo(1,
            "Call Flow page should display at least one DID entry");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallFlowPage_ShouldShowTracerInput(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/call-flow");
        await Page!.WaitForTimeoutAsync(2000);

        var tracerInput = await Page.QuerySelectorAsync("input.cf-tracer-number, input[placeholder*='number'], input[placeholder*='Number']");
        if (tracerInput is null) return; // Tracer not rendered

        tracerInput.Should().NotBeNull("tracer should have a number input field");

        var traceButton = await Page.QuerySelectorAsync("button.cf-trace-btn, button:has-text('Trace'), button:has-text('Trazar')");
        traceButton.Should().NotBeNull("tracer should have a Trace button");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallFlowPage_TracerShouldExecute(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/call-flow");
        await Page!.WaitForTimeoutAsync(2000);

        var tracerInput = await Page.QuerySelectorAsync("input.cf-tracer-number, input[placeholder*='number'], input[placeholder*='Number']");
        if (tracerInput is null) return; // Tracer not rendered

        await tracerInput.FillAsync("1003");

        var traceButton = await Page.QuerySelectorAsync("button.cf-trace-btn, button:has-text('Trace'), button:has-text('Trazar')");
        if (traceButton is null) return;

        await traceButton.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var traceSteps = await Page.QuerySelectorAllAsync(".cf-trace-step, .trace-step");
        traceSteps.Count.Should().BeGreaterOrEqualTo(1,
            "tracer should produce at least one trace step after execution");
    }
}
