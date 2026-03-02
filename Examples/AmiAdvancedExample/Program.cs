// Asterisk.Sdk - Advanced AMI Example
// Demonstrates: multiple AMI actions (Originate, Hangup, Command, QueueAdd/Remove),
// event filtering by type, and action/response correlation.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - Advanced AMI Example");
Console.WriteLine("======================================");

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
var ami = provider.GetRequiredService<IAmiConnection>();

try
{
    // 2. Connect
    Console.WriteLine("Connecting to Asterisk AMI...");
    await ami.ConnectAsync();
    Console.WriteLine($"Connected! Version: {ami.AsteriskVersion}");

    // 3. Subscribe with event type filtering
    using var subscription = ami.Subscribe(new FilteredEventObserver("Newchannel", "Hangup", "QueueMemberAdded"));

    // 4. Send a PingAction and correlate response
    Console.WriteLine("\n--- Sending PingAction ---");
    var pingResponse = await ami.SendActionAsync(new PingAction());
    Console.WriteLine($"Ping: {pingResponse.Response} (ActionId: {pingResponse.ActionId})");

    // 5. Execute a CLI command
    Console.WriteLine("\n--- Executing CLI Command ---");
    var cmdResponse = await ami.SendActionAsync(new CommandAction { Command = "core show uptime" });
    Console.WriteLine($"Command response: {cmdResponse.Response}");

    // 6. QueueAdd — add a member to a queue
    Console.WriteLine("\n--- Adding queue member ---");
    var queueAddResponse = await ami.SendActionAsync(new QueueAddAction
    {
        Queue = "support",
        Interface = "PJSIP/2000",
        MemberName = "Agent 2000",
        Penalty = 1
    });
    Console.WriteLine($"QueueAdd: {queueAddResponse.Response} - {queueAddResponse.Message}");

    // 7. QueueRemove — remove a member from a queue
    Console.WriteLine("\n--- Removing queue member ---");
    var queueRemoveResponse = await ami.SendActionAsync(new QueueRemoveAction
    {
        Queue = "support",
        Interface = "PJSIP/2000"
    });
    Console.WriteLine($"QueueRemove: {queueRemoveResponse.Response} - {queueRemoveResponse.Message}");

    // 8. Originate a call
    Console.WriteLine("\n--- Originating call ---");
    var originateResponse = await ami.SendActionAsync(new OriginateAction
    {
        Channel = "PJSIP/2000",
        Context = "default",
        Exten = "100",
        Priority = 1,
        CallerId = "Test <5551234>",
        Timeout = 30000
    });
    Console.WriteLine($"Originate: {originateResponse.Response} - {originateResponse.Message}");

    // 9. Event-generating action: list all active channels
    Console.WriteLine("\n--- Active Channels (StatusAction) ---");
    var channelCount = 0;
    await foreach (var evt in ami.SendEventGeneratingActionAsync(new StatusAction()))
    {
        if (evt is StatusEvent se)
        {
            channelCount++;
            Console.WriteLine($"  Channel: {se.Channel} State: {se.State} CallerID: {se.CallerId}");
        }
    }
    Console.WriteLine($"Total active channels: {channelCount}");

    // 10. Wait for events
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
    await ami.DisconnectAsync();
    Console.WriteLine("Disconnected.");
}

/// <summary>
/// Event observer that only prints events matching specified event types.
/// </summary>
sealed class FilteredEventObserver(params string[] allowedTypes) : IObserver<ManagerEvent>
{
    private readonly HashSet<string> _filter = new(allowedTypes, StringComparer.OrdinalIgnoreCase);

    public void OnNext(ManagerEvent value)
    {
        if (value.EventType is not null && _filter.Contains(value.EventType))
            Console.WriteLine($"[{value.EventType}] UniqueId={value.UniqueId} Channel={value.RawFields?.GetValueOrDefault("Channel")}");
    }

    public void OnError(Exception error) =>
        Console.Error.WriteLine($"[Error] {error.Message}");

    public void OnCompleted() =>
        Console.WriteLine("[Completed] Event stream ended.");
}
