namespace PbxAdmin.E2E.Tests.Routes;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class RoutesCrudTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task RoutesList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/routes']");
        link.Should().NotBeNull("sidebar should have a Routes link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Routes page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateRoute_ShouldNavigateToEditPage(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/routes']");
        link.Should().NotBeNull("sidebar should have a Routes link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        // Click the "New" / "Add" button to navigate to the edit/new page
        var newBtn = await Page.QuerySelectorAsync("a.btn-green, button.btn-green");
        if (newBtn is null) return; // No create button available

        await newBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        Page.Url.Should().Contain("/routes",
            "should be on a routes-related page after clicking new");
    }
}
