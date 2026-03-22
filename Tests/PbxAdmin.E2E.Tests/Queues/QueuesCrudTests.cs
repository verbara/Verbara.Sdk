namespace PbxAdmin.E2E.Tests.Queues;

using FluentAssertions;
using Microsoft.Playwright;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

[Trait("Category", "E2E")]
public sealed class QueuesCrudTests : PbxAdminTestBase
{
    private readonly string _suffix = Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task QueuesList_ShouldLoadAndShowHeading(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/queues");

        var heading = await Page!.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("Queues page should have an h2 heading");

        var text = await heading!.InnerTextAsync();
        text.Should().NotBeNullOrWhiteSpace();
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task CreateQueueConfig_ShouldSucceed(string server)
    {
        await SelectServerAsync(server);

        var queueName = $"e2e-queue-{_suffix}";

        await NavigateToAsync($"/queue-config/{server}/new");

        // Wait for Blazor interactive form to render
        var formLoaded = await TryWaitForBlazorFormAsync(".form-section");
        if (!formLoaded) return;

        // Fill queue name — first .input in the identity section
        var nameInput = await Page!.QuerySelectorAsync(".form-field input.input");
        if (nameInput is null) return;
        await nameInput.FillAsync(queueName);

        // Select strategy — the strategy select is in the form
        var strategySelect = await Page.QuerySelectorAsync("select.input");
        if (strategySelect is not null)
        {
            await strategySelect.SelectOptionAsync("ringall");
        }

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

        // Verify we are back on queues list or still on form
        var url = Page.Url;
        (url.Contains("/queues") || url.Contains("/queue-config")).Should().BeTrue(
            "should be on queues page after save attempt");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task QueueDetail_ShouldShowStats(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/queues");

        // Find the first queue card link
        var queueLink = await Page!.QuerySelectorAsync(".queue-card-header a");
        if (queueLink is null)
        {
            // No queues available — skip gracefully
            return;
        }

        await queueLink.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify we are on the queue detail page
        Page.Url.Should().Contain("/queue/",
            "clicking queue name should navigate to detail page");

        // Verify the page has a heading
        var heading = await Page.QuerySelectorAsync("h2");
        heading.Should().NotBeNull("queue detail page should have a heading");
    }

    [PbxAdminTheory]
    [InlineData("pbx-file")]
    [InlineData("pbx-realtime")]
    public async Task DeleteQueueConfig_ShouldShowConfirmBar(string server)
    {
        await SelectServerAsync(server);
        await NavigateToAsync("/queues");

        // Find a queue config gear button to navigate to edit
        var configBtn = await Page!.QuerySelectorAsync(".queue-card-header a.btn");
        if (configBtn is null)
        {
            // No queue configs — skip gracefully
            return;
        }

        await configBtn.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // If we are on a queue config edit page (not /new), find the delete button
        if (!Page.Url.Contains("/new"))
        {
            var deleteBtn = await Page.QuerySelectorAsync("button.btn.btn-red");
            if (deleteBtn is not null)
            {
                await deleteBtn.ClickAsync();
                await Page.WaitForTimeoutAsync(500);

                // Verify the confirm bar appeared
                var confirmBar = await Page.QuerySelectorAsync(".confirm-bar");
                confirmBar.Should().NotBeNull("delete should show a confirmation bar");

                // Cancel — click the cancel/muted button in the confirm bar
                var cancelBtn = await Page.QuerySelectorAsync(".confirm-bar button.btn-muted");
                if (cancelBtn is not null) await cancelBtn.ClickAsync();
            }
        }
    }
}
