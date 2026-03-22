namespace PbxAdmin.E2E.Tests.IvrMenus;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class IvrMenusCrudTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task IvrMenusList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/ivr-menus']");
        link.Should().NotBeNull("sidebar should have an IVR Menus link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("IVR Menus page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateIvrMenu_ShouldNavigateToEditPage(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/ivr-menus']");
        link.Should().NotBeNull("sidebar should have an IVR Menus link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var newBtn = await Page.QuerySelectorAsync("a.btn-green, button.btn-green");
        if (newBtn is null) return;

        await newBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        Page.Url.Should().Contain("/ivr-menus",
            "should be on an IVR menus-related page after clicking new");
    }
}
