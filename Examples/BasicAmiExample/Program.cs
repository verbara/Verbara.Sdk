// Asterisk.Sdk - Basic AMI Example
// Demonstrates: DI setup, connect, PingAction, event subscription, disconnect.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - Basic AMI Example");
Console.WriteLine("===================================");

// 1. Configure services with DI
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAsterisk(options =>
{
    options.Ami.Hostname = "localhost";
    options.Ami.Port = 5038;
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});

await using var provider = services.BuildServiceProvider();

// 2. Resolve the AMI connection
var ami = provider.GetRequiredService<IAmiConnection>();

try
{
    // 3. Connect to Asterisk
    Console.WriteLine("Connecting to Asterisk AMI...");
    await ami.ConnectAsync();
    Console.WriteLine($"Connected! Asterisk version: {ami.AsteriskVersion}");

    // 4. Subscribe to events
    using var subscription = ami.Subscribe(new EventPrinter());

    // 5. Send a PingAction
    var response = await ami.SendActionAsync(new PingAction());
    Console.WriteLine($"Ping response: {response.Response} - {response.Message}");

    // 6. Wait for some events
    Console.WriteLine("Listening for events (press Ctrl+C to stop)...");
    await Task.Delay(Timeout.InfiniteTimeSpan, new CancellationTokenSource().Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
}
finally
{
    await ami.DisconnectAsync();
    Console.WriteLine("Disconnected.");
}

// Simple event observer that prints events to console
sealed class EventPrinter : IObserver<ManagerEvent>
{
    public void OnNext(ManagerEvent value) =>
        Console.WriteLine($"[Event] {value.EventType}: UniqueId={value.UniqueId}");

    public void OnError(Exception error) =>
        Console.Error.WriteLine($"[Error] {error.Message}");

    public void OnCompleted() =>
        Console.WriteLine("[Completed] Event stream ended.");
}
