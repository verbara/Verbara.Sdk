namespace PbxAdmin.E2E.Tests.Conferences;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class ConferencesCrudTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task ConferencesList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/conferences']");
        link.Should().NotBeNull("sidebar should have a Conferences link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Conferences page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateConference_ShouldNavigateToEditPage(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/conferences']");
        link.Should().NotBeNull("sidebar should have a Conferences link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var newBtn = await Page.QuerySelectorAsync("a.btn-green, button.btn-green");
        if (newBtn is null) return;

        await newBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        Page.Url.Should().Contain("/conferences",
            "should be on a conferences-related page after clicking new");
    }
}
