namespace PbxAdmin.E2E.Tests.TrunkEmulation;

using Xunit;

/// <summary>
/// Skips the test if both PbxAdmin (port 8080) and AMI on pbx-file (port 5039)
/// are not reachable, or if Playwright browsers are not installed.
/// </summary>
public sealed class TrunkEmulationFactAttribute : FactAttribute
{
    private static readonly string? s_skipReason = DetectSkipReason();

    public TrunkEmulationFactAttribute()
    {
        if (s_skipReason is not null)
            Skip = s_skipReason;
    }

    private static string? DetectSkipReason()
    {
        if (!IsPbxAdminReachable())
            return "PbxAdmin not reachable at http://localhost:8080/";

        if (!IsAmiReachable())
            return "AMI not reachable on localhost:5039 (demo-pbx-file)";

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

    private static bool IsAmiReachable()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect("localhost", 5039);
            return true;
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
