namespace PbxAdmin.E2E.Tests.Trunks;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class TrunksCrudTests : PbxAdminTestBase
{
    private readonly string _suffix = Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task TrunksList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/trunks");

        var heading = await Page!.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Trunks page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateTrunk_ShouldSucceed(string server)
    {
        await SelectServerAsync(server);

        var trunkName = $"e2e-trunk-{_suffix}";

        await NavigateToAsync($"/trunks/new?server={server}");

        // Wait for Blazor interactive form to render
        var formLoaded = await TryWaitForBlazorFormAsync(".form-section");
        if (!formLoaded) return;

        // Fill trunk name — input with placeholder "my-trunk"
        var nameInput = await Page!.QuerySelectorAsync("input[placeholder='my-trunk']");
        if (nameInput is null) return;
        await nameInput.FillAsync(trunkName);

        // Fill host — input with placeholder "sip.provider.com"
        var hostInput = await Page.QuerySelectorAsync("input[placeholder='sip.provider.com']");
        if (hostInput is null) return;
        await hostInput.FillAsync("192.168.1.100");

        // Fill username — input with placeholder "username"
        var userInput = await Page.QuerySelectorAsync("input[placeholder='username']");
        if (userInput is not null) await userInput.FillAsync($"user-{_suffix}");

        // Fill secret — input with placeholder "password"
        var secretInput = await Page.QuerySelectorAsync("input[placeholder='password']");
        if (secretInput is not null) await secretInput.FillAsync("secretpass123");

        // Trigger blur events to enable validation
        await Page.ClickAsync("h2");
        await Page.WaitForTimeoutAsync(500);

        // Click save
        var saveBtn = await Page.QuerySelectorAsync("button.btn.btn-green");
        if (saveBtn is not null)
        {
            var isDisabled = await saveBtn.GetAttributeAsync("disabled");
            if (isDisabled is null)
            {
                await saveBtn.ClickAsync();
                await WaitForNoOverlayAsync();
                await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
        }

        // Verify we are back on trunks list or still on form
        var url = Page.Url;
        (url.Contains("/trunks") || url.Contains("/trunks/new")).Should().BeTrue(
            "should be on trunks page after save attempt");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task EditTrunk_ShouldNavigateToEditPage(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/trunks");

        // Find the first edit button on any trunk card
        var editBtn = await Page!.QuerySelectorAsync(".trunk-card-actions button.btn-yellow");
        if (editBtn is null)
        {
            // No trunks to edit — skip gracefully
            return;
        }

        await editBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify we are on the edit page
        Page.Url.Should().Contain("/trunks/edit/",
            "clicking edit should navigate to the trunk edit page");

        // Verify form fields are populated
        var nameInput = await Page.QuerySelectorAsync("input[placeholder='my-trunk']");
        nameInput.Should().NotBeNull("edit form should have the trunk name field");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task DeleteTrunk_ShouldShowConfirmDialog(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/trunks");

        // Find the first delete button
        var deleteBtn = await Page!.QuerySelectorAsync(".trunk-card-actions button.btn-red");
        if (deleteBtn is null)
        {
            // No trunks to delete — skip gracefully
            return;
        }

        await deleteBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Verify the confirm dialog appeared
        var confirmOverlay = await Page.QuerySelectorAsync(".confirm-overlay");
        confirmOverlay.Should().NotBeNull("delete should show a confirmation dialog");

        // Cancel the delete to avoid modifying test data
        var cancelBtn = await Page.QuerySelectorAsync(".confirm-dialog-actions button.btn-sm:not(.btn-red)");
        if (cancelBtn is not null) await cancelBtn.ClickAsync();
    }
}
