// Asterisk.Sdk - ARI Channel Control Example
// Demonstrates: ARI REST (originate, bridge, add channels, play media, hangup),
// WebSocket event subscription with StasisStart/StasisEnd filtering.

using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - ARI Channel Control Example");
Console.WriteLine("=============================================");

// 1. Configure services
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAsterisk(options =>
{
    options.Ami.Hostname = "localhost";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    options.Ari = new AriClientOptions
    {
        BaseUrl = "http://localhost:8088",
        Username = "asterisk",
        Password = "asterisk",
        Application = "channel-control-demo"
    };
});

await using var provider = services.BuildServiceProvider();
var ari = provider.GetRequiredService<IAriClient>();

try
{
    // 2. Connect to ARI WebSocket for events
    Console.WriteLine("Connecting to ARI...");
    await ari.ConnectAsync();
    Console.WriteLine("ARI WebSocket connected.");

    // 3. Subscribe to events, filtering StasisStart/StasisEnd
    using var subscription = ari.Subscribe(new AriEventPrinter());

    // 4. List existing channels
    Console.WriteLine("\n--- Existing Channels ---");
    var channels = await ari.Channels.ListAsync();
    foreach (var ch in channels)
        Console.WriteLine($"  {ch.Id}: {ch.Name} ({ch.State})");
    Console.WriteLine($"Total: {channels.Length} channels");

    // 5. Create a bridge for mixing
    Console.WriteLine("\n--- Creating Bridge ---");
    var bridge = await ari.Bridges.CreateAsync("mixing", "demo-bridge");
    Console.WriteLine($"Bridge created: {bridge.Id} type={bridge.BridgeType}");

    // 6. Originate a channel into the Stasis application
    Console.WriteLine("\n--- Originating Channel ---");
    var newChannel = await ari.Channels.CreateAsync("PJSIP/2000", "channel-control-demo");
    Console.WriteLine($"Channel created: {newChannel.Id} ({newChannel.Name})");

    // 7. Add channel to bridge
    Console.WriteLine("\n--- Adding Channel to Bridge ---");
    await ari.Bridges.AddChannelAsync(bridge.Id, newChannel.Id);
    Console.WriteLine($"Channel {newChannel.Id} added to bridge {bridge.Id}");

    // 8. Play media on the bridge
    Console.WriteLine("\n--- Playing Media ---");
    // Note: This would play audio — requires a sound file on the Asterisk server.
    // var playback = await ari.Channels.OriginateAsync("PJSIP/2000");

    // 9. Manage playbacks
    Console.WriteLine("\n--- Playback Operations ---");
    Console.WriteLine("(Skipped — requires active playback)");

    // 10. Clean up: remove channel from bridge, hangup, destroy bridge
    Console.WriteLine("\n--- Cleanup ---");
    await ari.Bridges.RemoveChannelAsync(bridge.Id, newChannel.Id);
    Console.WriteLine("Channel removed from bridge.");

    await ari.Channels.HangupAsync(newChannel.Id);
    Console.WriteLine("Channel hung up.");

    await ari.Bridges.DestroyAsync(bridge.Id);
    Console.WriteLine("Bridge destroyed.");

    // 11. Wait for more events
    Console.WriteLine("\nListening for events (press Ctrl+C to stop)...");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
}
finally
{
    await ari.DisconnectAsync();
    Console.WriteLine("ARI disconnected.");
}

/// <summary>
/// Prints ARI events, highlighting StasisStart and StasisEnd.
/// </summary>
sealed class AriEventPrinter : IObserver<AriEvent>
{
    public void OnNext(AriEvent value)
    {
        var prefix = value.Type switch
        {
            "StasisStart" => "[STASIS START]",
            "StasisEnd" => "[STASIS END]",
            _ => $"[{value.Type}]"
        };
        Console.WriteLine($"{prefix} app={value.Application} ts={value.Timestamp:HH:mm:ss}");
    }

    public void OnError(Exception error) =>
        Console.Error.WriteLine($"[ARI Error] {error.Message}");

    public void OnCompleted() =>
        Console.WriteLine("[ARI] Event stream ended.");
}
