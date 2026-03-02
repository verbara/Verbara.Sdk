// Asterisk.Sdk - Activities Example
// Demonstrates: high-level telephony operations using the Activities layer.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - Activities Example");
Console.WriteLine("====================================");

// 1. Configure services
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddAsterisk(options =>
{
    options.Ami.Hostname = "localhost";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();

var ami = provider.GetRequiredService<IAmiConnection>();
var server = provider.GetRequiredService<Asterisk.Sdk.Live.Server.AsteriskServer>();

try
{
    // 2. Connect and start tracking
    await ami.ConnectAsync();
    await server.StartAsync();
    Console.WriteLine($"Connected to Asterisk {ami.AsteriskVersion}");

    // 3. Originate a call using the Live API
    Console.WriteLine("Originating call: SIP/2000 -> extension 100...");
    var result = await server.OriginateAsync(
        channel: "SIP/2000",
        context: "default",
        extension: "100",
        callerId: "SDK Example <5551234>");

    Console.WriteLine(result.Success
        ? $"Call originated successfully! Channel: {result.ChannelId}"
        : $"Call failed: {result.Message}");

    // 4. Show active channels
    Console.WriteLine($"\nActive channels: {server.Channels.ChannelCount}");
    foreach (var ch in server.Channels.ActiveChannels)
    {
        Console.WriteLine($"  {ch.Name} ({ch.State})");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
}
finally
{
    await server.DisposeAsync();
    await ami.DisconnectAsync();
    Console.WriteLine("Disconnected.");
}
