// Asterisk.Sdk - Multi-Server Example
// Demonstrates: AddAsteriskMultiServer(), AsteriskServerPool with federated
// agent routing, AddServer, GetServerForAgent, RemoveServer.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Live.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - Multi-Server Example");
Console.WriteLine("=====================================");

// 1. Configure services with multi-server support
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAsteriskMultiServer();

await using var provider = services.BuildServiceProvider();
var pool = provider.GetRequiredService<AsteriskServerPool>();
var logger = provider.GetRequiredService<ILogger<Program>>();

try
{
    // 2. Add multiple Asterisk servers to the pool
    Console.WriteLine("\n--- Adding Asterisk Servers ---");

    var server1 = await pool.AddServerAsync("pbx-east", new AmiConnectionOptions
    {
        Hostname = "pbx-east.example.com",
        Port = 5038,
        Username = "admin",
        Password = "secret"
    });
    Console.WriteLine($"Added server 'pbx-east' - Asterisk {server1.AsteriskVersion}");
    Console.WriteLine($"  Channels: {server1.Channels.ChannelCount}, Agents: {server1.Agents.AgentCount}");

    var server2 = await pool.AddServerAsync("pbx-west", new AmiConnectionOptions
    {
        Hostname = "pbx-west.example.com",
        Port = 5038,
        Username = "admin",
        Password = "secret"
    });
    Console.WriteLine($"Added server 'pbx-west' - Asterisk {server2.AsteriskVersion}");
    Console.WriteLine($"  Channels: {server2.Channels.ChannelCount}, Agents: {server2.Agents.AgentCount}");

    // 3. Pool statistics
    Console.WriteLine($"\nPool: {pool.ServerCount} servers, {pool.TotalAgentCount} total agents");

    // 4. Federated agent lookup
    Console.WriteLine("\n--- Federated Agent Lookup ---");
    var agentId = "1001";
    var owningServer = pool.GetServerForAgent(agentId);
    if (owningServer is not null)
    {
        Console.WriteLine($"Agent {agentId} is on server with version {owningServer.AsteriskVersion}");
    }
    else
    {
        Console.WriteLine($"Agent {agentId} not found on any server.");
    }

    // 5. Enumerate all servers
    Console.WriteLine("\n--- All Servers ---");
    foreach (var (id, server) in pool.Servers)
    {
        Console.WriteLine($"  [{id}] Channels: {server.Channels.ChannelCount}, Queues: {server.Queues.QueueCount}, Agents: {server.Agents.AgentCount}");
    }

    // 6. Get a specific server by ID
    Console.WriteLine("\n--- Lookup by Server ID ---");
    var eastServer = pool.GetServer("pbx-east");
    if (eastServer is not null)
        Console.WriteLine($"Found 'pbx-east': {eastServer.Channels.ChannelCount} channels");

    // 7. Originate a call on a specific server
    Console.WriteLine("\n--- Originate on pbx-east ---");
    if (eastServer is not null)
    {
        var result = await eastServer.OriginateAsync(
            "PJSIP/2000", "default", "100",
            callerId: "MultiServer <5559999>",
            timeout: TimeSpan.FromSeconds(30));
        Console.WriteLine($"Originate result: {(result.Success ? "Success" : "Failed")} - {result.Message}");
    }

    // 8. Wait for events across all servers
    Console.WriteLine("\nMonitoring all servers (press Ctrl+C to stop)...");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // 9. Remove a server from the pool
    Console.WriteLine("\n--- Removing pbx-west ---");
    await pool.RemoveServerAsync("pbx-west");
    Console.WriteLine($"Pool now has {pool.ServerCount} server(s).");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
}
finally
{
    // 10. Dispose cleans up all servers
    await pool.DisposeAsync();
    Console.WriteLine("All servers disconnected.");
}
