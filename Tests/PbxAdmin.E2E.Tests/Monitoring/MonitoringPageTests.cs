namespace PbxAdmin.E2E.Tests.Monitoring;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class MonitoringPageTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task HomePage_ShouldShowDashboard(string server)
    {
        await SelectServerAsync(server);

        // The home page is loaded after server selection; verify dashboard content
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var bodyText = await Page.InnerTextAsync("body");
        bodyText.Should().NotBeNullOrWhiteSpace("dashboard should display content");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CallsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/calls']");
        link.Should().NotBeNull("sidebar should have a Calls link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Calls page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task ChannelsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/channels']");
        link.Should().NotBeNull("sidebar should have a Channels link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Channels page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task AgentsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/agents']");
        link.Should().NotBeNull("sidebar should have an Agents link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Agents page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task MetricsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/metrics']");
        link.Should().NotBeNull("sidebar should have a Metrics link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Metrics page should have an h2 heading");
    }
}
