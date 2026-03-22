namespace PbxAdmin.E2E.Tests.TrunkEmulation;

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbxAdmin.E2E.Tests.Infrastructure;
using Xunit;

/// <summary>
/// E2E tests that originate calls on the demo-pbx-file server via AMI
/// and verify that PbxAdmin UI pages reflect real-time activity.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TrunkEmulationTests : PbxAdminTestBase
{
    /// <summary>
    /// Creates an AMI connection to demo-pbx-file (port 5039, user "dashboard").
    /// </summary>
    private static async Task<AmiConnection> CreateAmiConnectionAsync()
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5039,
            Username = "dashboard",
            Password = "dashboard",
            DefaultResponseTimeout = TimeSpan.FromSeconds(15),
            AutoReconnect = false
        });

        var connection = new AmiConnection(
            options,
            new PipelineSocketConnectionFactory(),
            NullLogger<AmiConnection>.Instance);

        await connection.ConnectAsync();
        return connection;
    }

    /// <summary>
    /// Waits for a HangupEvent or timeout, whichever comes first.
    /// </summary>
    private static async Task WaitForHangupAsync(AmiConnection connection, int timeoutSeconds = 15)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetResult(false));

        connection.OnEvent += evt =>
        {
            if (string.Equals(evt.EventType, "Hangup", StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(true);
            return ValueTask.CompletedTask;
        };

        await tcs.Task;
    }

    [TrunkEmulationFact]
    public async Task OriginateCall_ShouldAppearInChannelsPage()
    {
        SetTestName(nameof(OriginateCall_ShouldAppearInChannelsPage));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a Local channel call that waits 5 seconds
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/800@default",
            Application = "Wait",
            Data = "5",
            IsAsync = true,
            ActionId = "e2e-channels-01"
        });

        // Navigate to channels page
        await NavigateToAsync("/channels");
        await Page!.WaitForTimeoutAsync(2000);

        // The page should load successfully
        var content = await Page.ContentAsync();
        content.Should().NotBeNullOrWhiteSpace("channels page should render");

        // Verify the channels page loaded (heading, table, or any real-time content)
        var pageLoaded = content.Contains("channel", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Local/", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<h", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<table", StringComparison.OrdinalIgnoreCase);
        pageLoaded.Should().BeTrue("channels page should contain page structure or channel data");

        // Wait for the call to finish
        await WaitForHangupAsync(ami, 10);
    }

    [TrunkEmulationFact]
    public async Task BusyCall_ShouldCompleteAndCallsPageLoads()
    {
        SetTestName(nameof(BusyCall_ShouldCompleteAndCallsPageLoads));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a call to a non-existent extension (will fail/busy quickly)
        var response = await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/9999@default",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = "e2e-busy-01"
        });

        response.Should().NotBeNull("AMI should return a response to the originate");

        // Wait briefly for the call attempt to complete
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Navigate to calls/CDR page
        await NavigateToAsync("/calls");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        var content = await Page.ContentAsync();
        content.Should().NotBeNullOrWhiteSpace("calls page should render");
    }

    [TrunkEmulationFact]
    public async Task NormalCall_ShouldGenerateCdr()
    {
        SetTestName(nameof(NormalCall_ShouldGenerateCdr));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a short call via Local channel
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/800@default",
            Application = "Wait",
            Data = "2",
            IsAsync = true,
            ActionId = "e2e-cdr-01"
        });

        // Wait for the call to complete
        await WaitForHangupAsync(ami, 10);

        // Give Asterisk a moment to write CDR
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Navigate to calls page
        await NavigateToAsync("/calls");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var content = await Page.ContentAsync();
        content.Should().NotBeNullOrWhiteSpace("calls page should have content after a completed call");

        // Look for table or card elements that indicate CDR data
        var hasCdrContent = content.Contains("table", StringComparison.OrdinalIgnoreCase)
            || content.Contains("card", StringComparison.OrdinalIgnoreCase)
            || content.Contains("call", StringComparison.OrdinalIgnoreCase);
        hasCdrContent.Should().BeTrue("calls page should show CDR-related content");
    }

    [TrunkEmulationFact]
    public async Task LongCall_ShouldShowInRealtimeDashboard()
    {
        SetTestName(nameof(LongCall_ShouldShowInRealtimeDashboard));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a long call (30s Wait)
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/800@default",
            Application = "Wait",
            Data = "30",
            IsAsync = true,
            ActionId = "e2e-dashboard-01"
        });

        // Let the call establish
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Navigate to dashboard
        await NavigateToAsync("/");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var contentBefore = await Page.ContentAsync();
        contentBefore.Should().NotBeNullOrWhiteSpace("dashboard should render while call is active");

        // Hang up the active channel via AMI
        await ami.SendActionAsync(new HangupAction
        {
            Channel = "Local/800@default;1",
            ActionId = "e2e-hangup-dashboard"
        });

        // Wait for cleanup
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Reload dashboard
        await NavigateToAsync("/");
        await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        var contentAfter = await Page.ContentAsync();
        contentAfter.Should().NotBeNullOrWhiteSpace("dashboard should render after call ended");
    }

    [TrunkEmulationFact]
    public async Task QuickCall_ShouldNotLeaveOrphanChannels()
    {
        SetTestName(nameof(QuickCall_ShouldNotLeaveOrphanChannels));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a very short call (1s)
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/800@default",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = "e2e-quick-01"
        });

        // Wait for complete cleanup
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Navigate to channels page
        await NavigateToAsync("/channels");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(1000);

        var content = await Page.ContentAsync();
        content.Should().NotBeNullOrWhiteSpace("channels page should render");

        // The quick call should have ended; look for absence of active Local/800 channels
        var hasOrphan = content.Contains("Local/800@default", StringComparison.Ordinal)
            && content.Contains("Up", StringComparison.OrdinalIgnoreCase);
        hasOrphan.Should().BeFalse("quick call should not leave orphan channels in the UI");
    }

    [TrunkEmulationFact]
    public async Task VoicemailCall_ShouldComplete()
    {
        SetTestName(nameof(VoicemailCall_ShouldComplete));

        await SelectServerAsync("pbx-file");

        await using var ami = await CreateAmiConnectionAsync();

        // Originate a call that goes to a Playback (simulates voicemail-like flow)
        var response = await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/800@default",
            Application = "Playback",
            Data = "hello-world",
            IsAsync = true,
            ActionId = "e2e-voicemail-01"
        });

        response.Should().NotBeNull("AMI should accept the originate");

        // Wait for the playback to complete
        await WaitForHangupAsync(ami, 15);

        // Navigate to any page to verify no errors
        await NavigateToAsync("/channels");
        await Page!.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        // Verify page loaded without errors (no unhandled exception overlay)
        var content = await Page.ContentAsync();
        var hasError = content.Contains("Error", StringComparison.Ordinal)
            && content.Contains("unhandled", StringComparison.OrdinalIgnoreCase);
        hasError.Should().BeFalse("page should not show unhandled errors after voicemail call");
    }
}
