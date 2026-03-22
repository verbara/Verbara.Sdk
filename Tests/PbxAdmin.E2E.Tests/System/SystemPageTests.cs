namespace PbxAdmin.E2E.Tests.SystemPages;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class SystemPageTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task EventsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/events']");
        link.Should().NotBeNull("sidebar should have an Events link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Events page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task ConsolePage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/console']");
        link.Should().NotBeNull("sidebar should have a Console link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Console page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task TrafficPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/traffic']");
        link.Should().NotBeNull("sidebar should have a Traffic link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Traffic page should have an h2 heading");
    }
}
