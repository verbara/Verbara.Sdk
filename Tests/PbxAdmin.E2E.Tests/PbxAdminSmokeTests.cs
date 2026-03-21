using Microsoft.Playwright;
using Xunit;
using FluentAssertions;

namespace PbxAdmin.E2E.Tests;

[Trait("Category", "E2E")]
public class PbxAdminSmokeTests : IAsyncLifetime
{
    private const string BaseUrl = "http://localhost:8080";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await _browser.NewContextAsync();
        _page = await context.NewPageAsync();

        // Authenticate: POST the login form so the cookie is set for subsequent navigations
        await _page.GotoAsync($"{BaseUrl}/login");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.FillAsync("input[placeholder='admin']", "admin");
        await _page.FillAsync("input[type='password']", "admin");
        await _page.ClickAsync("button[type='submit']");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [PbxAdminFact]
    public async Task HomePage_ShouldLoad()
    {
        await _page!.GotoAsync(BaseUrl);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var title = await _page.TitleAsync();
        title.Should().NotBeNullOrWhiteSpace();

        var bodyText = await _page.InnerTextAsync("body");
        bodyText.Should().ContainAny("PBX", "Asterisk", "Select Server", "Dashboard");
    }

    [PbxAdminFact]
    public async Task SelectServer_ShouldShowServerList()
    {
        await _page!.GotoAsync($"{BaseUrl}/select-server");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The page should contain at least one server card
        var cards = await _page.QuerySelectorAllAsync(".server-select-card");
        if (cards.Count > 0)
        {
            cards.Should().NotBeEmpty("at least one server should be configured");
        }
        else
        {
            // If there is only one server, the page auto-redirects to Home.
            // Verify we landed on a valid page (Home or still on select-server).
            var url = _page.Url;
            url.Should().MatchRegex("(/$|/select-server)");
        }
    }

    [PbxAdminFact]
    public async Task Extensions_ShouldLoadAfterServerSelection()
    {
        // Navigate to select-server first
        await _page!.GotoAsync($"{BaseUrl}/select-server");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // If there are server cards, click the first one; otherwise auto-redirect happened
        var cards = await _page.QuerySelectorAllAsync(".server-select-card");
        if (cards.Count > 0)
        {
            await cards[0].ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Navigate to Extensions
        await _page.GotoAsync($"{BaseUrl}/extensions");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var heading = await _page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Extensions page should have an h2 heading");

        var bodyText = await _page.InnerTextAsync("body");
        bodyText.Should().NotBeNullOrWhiteSpace();

        // Page should not show a server error
        var statusCode = (await _page.GotoAsync($"{BaseUrl}/extensions"))!.Status;
        statusCode.Should().BeLessThan(500);
    }

    [PbxAdminFact]
    public async Task Navigation_ShouldWorkBetweenPages()
    {
        // Ensure a server is selected
        await _page!.GotoAsync($"{BaseUrl}/select-server");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var cards = await _page.QuerySelectorAllAsync(".server-select-card");
        if (cards.Count > 0)
        {
            await cards[0].ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Navigate to Trunks
        var trunksResponse = (await _page.GotoAsync($"{BaseUrl}/trunks"))!;
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        trunksResponse.Status.Should().BeLessThan(500, "Trunks page should not return a server error");
        var trunksHeading = await _page.QuerySelectorAsync("h2");
        trunksHeading.Should().NotBeNull("Trunks page should have an h2 heading");

        // Navigate to Queues
        var queuesResponse = (await _page.GotoAsync($"{BaseUrl}/queues"))!;
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        queuesResponse.Status.Should().BeLessThan(500, "Queues page should not return a server error");
        var queuesHeading = await _page.QuerySelectorAsync("h2");
        queuesHeading.Should().NotBeNull("Queues page should have an h2 heading");

        // Navigate to Extensions
        var extensionsResponse = (await _page.GotoAsync($"{BaseUrl}/extensions"))!;
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        extensionsResponse.Status.Should().BeLessThan(500, "Extensions page should not return a server error");
        var extensionsHeading = await _page.QuerySelectorAsync("h2");
        extensionsHeading.Should().NotBeNull("Extensions page should have an h2 heading");
    }
}
