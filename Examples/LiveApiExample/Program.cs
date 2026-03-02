// Asterisk.Sdk - Live API Example
// Demonstrates: real-time tracking of channels, queues, and agents via AMI events.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - Live API Example");
Console.WriteLine("==================================");

// 1. Configure services
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
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
    // 2. Connect to Asterisk AMI
    Console.WriteLine("Connecting to AMI...");
    await ami.ConnectAsync();
    Console.WriteLine($"Connected to Asterisk {ami.AsteriskVersion}");

    // 3. Start live tracking (subscribes to events + loads initial state)
    await server.StartAsync();

    // 4. Print current state
    Console.WriteLine($"Active channels: {server.Channels.ChannelCount}");
    Console.WriteLine($"Queues: {server.Queues.QueueCount}");
    Console.WriteLine($"Agents: {server.Agents.AgentCount}");

    // 5. Monitor changes
    Console.WriteLine("\nMonitoring state changes (press Ctrl+C to stop)...");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Channels={server.Channels.ChannelCount}, " +
                          $"Queues={server.Queues.QueueCount}, Agents={server.Agents.AgentCount}");
    }
}
catch (OperationCanceledException) { }
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
