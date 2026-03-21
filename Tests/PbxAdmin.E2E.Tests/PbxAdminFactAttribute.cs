using Xunit;

namespace PbxAdmin.E2E.Tests;

/// <summary>
/// Skips the test if PbxAdmin is not reachable at localhost:8080,
/// or if Playwright browsers are not installed.
/// </summary>
public sealed class PbxAdminFactAttribute : FactAttribute
{
    private static readonly string? s_skipReason = DetectSkipReason();

    public PbxAdminFactAttribute()
    {
        if (s_skipReason is not null)
            Skip = s_skipReason;
    }

    private static string? DetectSkipReason()
    {
        if (!IsPbxAdminReachable())
            return "PbxAdmin not reachable at http://localhost:8080/";

        if (!IsPlaywrightBrowserAvailable())
            return "Playwright Chromium browser not installed. Run: pwsh bin/Debug/net10.0/playwright.ps1 install chromium";

        return null;
    }

    private static bool IsPbxAdminReachable()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = client.GetAsync("http://localhost:8080/").GetAwaiter().GetResult();
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPlaywrightBrowserAvailable()
    {
        try
        {
            var playwright = Microsoft.Playwright.Playwright.CreateAsync().GetAwaiter().GetResult();
            var browser = playwright.Chromium.LaunchAsync(new() { Headless = true }).GetAwaiter().GetResult();
            browser.CloseAsync().GetAwaiter().GetResult();
            playwright.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
