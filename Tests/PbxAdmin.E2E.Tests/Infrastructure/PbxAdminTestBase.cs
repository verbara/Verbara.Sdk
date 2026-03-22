namespace PbxAdmin.E2E.Tests.Infrastructure;

using Microsoft.Playwright;
using Xunit;

public abstract class PbxAdminTestBase : IAsyncLifetime
{
    protected const string BaseUrl = "http://localhost:8080";
    protected IPlaywright? Playwright { get; private set; }
    protected IBrowser? Browser { get; private set; }
    protected IBrowserContext? Context { get; private set; }
    protected IPage? Page { get; private set; }

    private string? _videoPath;
    private string _testName = "unknown";

    /// <summary>
    /// Call at the start of each test to set the test name for video naming.
    /// </summary>
    protected void SetTestName(string name) => _testName = SanitizeFileName(name);

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        // SlowMo adds delay between each Playwright action — makes videos watchable
        Browser = await Playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            SlowMo = 300 // 300ms between each action
        });

        Context = await Browser.NewContextAsync(new()
        {
            RecordVideoDir = "test-videos/",
            RecordVideoSize = new() { Width = 1280, Height = 720 }
        });

        Page = await Context.NewPageAsync();
        await LoginAsync();
    }

    public async Task DisposeAsync()
    {
        // Capture the video path before closing context
        if (Page?.Video is not null)
        {
            _videoPath = await Page.Video.PathAsync();
        }

        if (Context is not null) await Context.CloseAsync(); // saves video

        // Rename video to descriptive test name
        if (_videoPath is not null && File.Exists(_videoPath))
        {
            var dir = Path.GetDirectoryName(_videoPath)!;
            var newPath = Path.Combine(dir, $"{_testName}.webm");

            // Avoid overwriting if duplicate name
            if (File.Exists(newPath))
                newPath = Path.Combine(dir, $"{_testName}_{Guid.NewGuid():N[..8]}.webm");

            try { File.Move(_videoPath, newPath); } catch { /* best effort */ }
        }

        if (Browser is not null) await Browser.CloseAsync();
        Playwright?.Dispose();
    }

    protected async Task LoginAsync()
    {
        await Page!.GotoAsync($"{BaseUrl}/login");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var inputs = await Page.QuerySelectorAllAsync("input.access-input");
        if (inputs.Count >= 2)
        {
            await inputs[0].FillAsync("admin");
            await inputs[1].FillAsync("admin");
        }

        var submit = await Page.QuerySelectorAsync("button.access-submit");
        if (submit is not null) await submit.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    protected async Task SelectServerAsync(string serverIdContains,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        // Auto-set test name for video file naming
        if (!string.IsNullOrEmpty(callerName))
            SetTestName($"{callerName}_{serverIdContains}");

        await Page!.GotoAsync($"{BaseUrl}/select-server");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var cards = await Page.QuerySelectorAllAsync(".server-select-card");
        foreach (var card in cards)
        {
            var text = await card.InnerTextAsync();
            if (text.Contains(serverIdContains, StringComparison.OrdinalIgnoreCase))
            {
                await card.ClickAsync();
                await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return;
            }
        }
    }

    protected async Task WaitForNoOverlayAsync()
    {
        try
        {
            await Page!.WaitForSelectorAsync(".config-overlay",
                new() { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            // Overlay might not have appeared
        }
    }

    protected async Task NavigateToAsync(string path)
    {
        await Page!.GotoAsync($"{BaseUrl}{path}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Waits for a Blazor Server interactive form to render.
    /// </summary>
    protected async Task<bool> TryWaitForBlazorFormAsync(string selector, int timeoutMs = 10_000)
    {
        try
        {
            await Page!.WaitForSelectorAsync(selector,
                new() { State = WaitForSelectorState.Attached, Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    protected async Task TakeScreenshotOnFailureAsync(string testName)
    {
        try
        {
            await Page!.ScreenshotAsync(new()
            {
                Path = $"test-videos/{SanitizeFileName(testName)}-failure.png",
                FullPage = true
            });
        }
        catch
        {
            // Best-effort screenshot
        }
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
