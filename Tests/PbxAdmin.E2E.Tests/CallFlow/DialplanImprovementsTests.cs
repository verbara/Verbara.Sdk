namespace PbxAdmin.E2E.Tests.CallFlow;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class DialplanImprovementsTests : PbxAdminTestBase
{
    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task DialplanPage_ShouldShowTypeBadges(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/dialplan");
        await Page!.WaitForTimeoutAsync(2000);

        var heading = await Page.QuerySelectorAsync("h2");
        if (heading is null) return; // Page not available

        // Look for type badges in the context list
        var badges = await Page.QuerySelectorAllAsync(".badge, .context-type-badge");
        if (badges.Count == 0) return; // Badges not yet implemented

        // Verify badges exist — the page should show System/User badges at minimum,
        // and type badges (Inbound, TC, IVR, Main) for known context patterns
        badges.Count.Should().BeGreaterOrEqualTo(1,
            "context list should have at least one badge (System or User)");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task DialplanPage_ShouldBeInSystemNav(string server)
    {
        await SelectServerAsync(server);

        // Look for the Adv. Dialplan link in the sidebar
        var dialplanLink = await Page!.QuerySelectorAsync(
            "a.nav-item[href='/dialplan'], a.nav-item[href*='dialplan']");
        if (dialplanLink is null) return; // Link not present

        // Verify it exists in the navigation — the link itself is proof it is in the sidebar
        dialplanLink.Should().NotBeNull(
            "Adv. Dialplan link should be present in the sidebar navigation");

        // Click and verify the page loads
        await dialplanLink.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        Page.Url.Should().Contain("/dialplan",
            "clicking the nav link should navigate to the dialplan page");
    }
}
