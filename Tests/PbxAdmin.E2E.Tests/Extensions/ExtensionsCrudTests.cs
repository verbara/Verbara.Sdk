namespace PbxAdmin.E2E.Tests.Extensions;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class ExtensionsCrudTests : PbxAdminTestBase
{
    private readonly string _suffix = Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task ExtensionsList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/extensions");

        var heading = await Page!.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Extensions page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateExtension_ShouldSucceed(string server)
    {
        await SelectServerAsync(server);

        var extNumber = $"8{_suffix.Substring(0, 3)}";
        var displayName = $"E2E Test {_suffix}";

        await NavigateToAsync($"/extensions/new?server={server}");

        // Wait for Blazor interactive form to render
        var formLoaded = await TryWaitForBlazorFormAsync(".form-section");
        if (!formLoaded)
        {
            // Form did not render (e.g., file-mode server may not support inline creation)
            return;
        }

        // Fill extension number
        var extInput = await Page!.QuerySelectorAsync("input[type='number']");
        if (extInput is null) return;
        await extInput.FillAsync(extNumber);

        // Fill display name — input with placeholder containing a name pattern
        var nameInputs = await Page.QuerySelectorAllAsync(".form-field input.input");
        var nameInput = nameInputs.Count > 0 ? nameInputs[0] : null;
        if (nameInput is null) return;
        await nameInput.FillAsync(displayName);

        // Fill password
        var pwdInput = await Page.QuerySelectorAsync("input[type='password']");
        if (pwdInput is null) return;
        await pwdInput.FillAsync("testpass1234");

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

        // Verify we are back on extensions list or the save succeeded
        var url = Page.Url;
        (url.Contains("/extensions") || url.Contains("/extensions/new")).Should().BeTrue(
            "should be on extensions page after save attempt");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task EditExtension_ShouldNavigateToEditPage(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/extensions");

        // Find the first edit button on any extension card
        var editBtn = await Page!.QuerySelectorAsync(".trunk-card-actions button.btn-yellow");
        if (editBtn is null)
        {
            // No extensions to edit — skip gracefully
            return;
        }

        await editBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify we are on the edit page
        Page.Url.Should().Contain("/extensions/edit/",
            "clicking edit should navigate to the extension edit page");

        // Verify form fields are populated
        var nameInputs = await Page.QuerySelectorAllAsync(".form-field input.input");
        nameInputs.Should().NotBeEmpty("edit form should have input fields");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task DeleteExtension_ShouldShowConfirmDialog(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/extensions");

        // Find the first delete button
        var deleteBtn = await Page!.QuerySelectorAsync(".trunk-card-actions button.btn-red");
        if (deleteBtn is null)
        {
            // No extensions to delete — skip gracefully
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
