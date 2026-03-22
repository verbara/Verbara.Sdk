namespace PbxAdmin.E2E.Tests.Media;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class MediaPageTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task RecordingsPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/recordings']");
        link.Should().NotBeNull("sidebar should have a Recordings link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Recordings page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task MohPage_ShouldLoad(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/moh']");
        link.Should().NotBeNull("sidebar should have a Music on Hold link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Music on Hold page should have an h2 heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task MohPage_ShouldShowDefaultClass(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/moh']");
        link.Should().NotBeNull("sidebar should have a Music on Hold link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var bodyText = await Page.InnerTextAsync("body");
        bodyText.Should().Contain("default", "Music on Hold page should show the default class");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task RecordingsPage_ShouldShowFiles(string server)
    {
        await SelectServerAsync(server);

        var link = await Page!.QuerySelectorAsync("a.nav-item[href='/recordings']");
        link.Should().NotBeNull("sidebar should have a Recordings link");
        await link!.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        // Click the Files tab if present
        var filesTab = await Page.QuerySelectorAsync("button:has-text('Files'), a:has-text('Files')");
        if (filesTab is not null)
        {
            await filesTab.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Page.WaitForTimeoutAsync(2000);
        }

        var bodyText = await Page.InnerTextAsync("body");
        bodyText.Should().Contain(".wav", "Recordings file list should contain .wav files");
    }
}
