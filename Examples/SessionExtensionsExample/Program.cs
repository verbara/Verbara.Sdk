// Asterisk.Sdk - Session Extensions Example
// Demonstrates: implementing a custom SessionStoreBase (FileSessionStore) that
// persists session snapshots as JSON lines to a file.

using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SessionExtensionsExample;
using System.Globalization;

Console.WriteLine("Asterisk.Sdk - Session Extensions Example");
Console.WriteLine("==========================================");
Console.WriteLine("Custom FileSessionStore persists sessions as JSON lines.\n");

// 1. Build host with Asterisk + Sessions
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = builder.Configuration["Asterisk:Hostname"] ?? "localhost";
    options.Ami.Username = builder.Configuration["Asterisk:Username"] ?? "admin";
    options.Ami.Password = builder.Configuration["Asterisk:Password"] ?? "secret";
});

// Register the custom FileSessionStore BEFORE AddAsteriskSessions so that
// TryAddSingleton<SessionStoreBase> sees our registration first.
var fileStore = new FileSessionStore("sessions.jsonl");
builder.Services.AddSingleton<SessionStoreBase>(fileStore);

builder.Services.AddAsteriskSessions();

var app = builder.Build();

var sessionManager = app.Services.GetRequiredService<ICallSessionManager>();

// 2. Subscribe to session domain events
using var subscription = sessionManager.Events.Subscribe(evt =>
{
    var id = evt.SessionId[..8];
    var ts = evt.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    switch (evt)
    {
        case CallStartedEvent started:
            WriteColored(ConsoleColor.Green,
                $"[{ts}] STARTED  {id}  {started.Direction}  Caller: {started.CallerIdNum ?? "(unknown)"}");
            break;

        case CallConnectedEvent connected:
            WriteColored(ConsoleColor.Cyan,
                $"[{ts}] CONNECT  {id}  Agent: {connected.AgentId ?? "-"}  " +
                $"Queue: {connected.QueueName ?? "-"}  Wait: {connected.WaitTime.TotalSeconds:F1}s");
            break;

        case CallHeldEvent:
            WriteColored(ConsoleColor.Magenta,
                $"[{ts}] HELD     {id}");
            break;

        case CallResumedEvent:
            WriteColored(ConsoleColor.Cyan,
                $"[{ts}] RESUMED  {id}");
            break;

        case CallEndedEvent ended:
            var talkTime = ended.TalkTime.HasValue
                ? $"  Talk: {ended.TalkTime.Value.TotalSeconds:F1}s"
                : "";
            WriteColored(ConsoleColor.Red,
                $"[{ts}] ENDED    {id}  Duration: {ended.Duration.TotalSeconds:F1}s{talkTime}");
            break;

        case CallFailedEvent failed:
            WriteColored(ConsoleColor.DarkRed,
                $"[{ts}] FAILED   {id}  Reason: {failed.Reason}");
            break;

        default:
            Console.WriteLine($"[{ts}] EVENT    {id}  {evt.GetType().Name}");
            break;
    }
});

// 3. Run until Ctrl+C, then print store summary
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("Monitoring sessions (press Ctrl+C to stop)...");
Console.WriteLine($"Sessions will be persisted to: {Path.GetFullPath("sessions.jsonl")}\n");

try
{
    await app.RunAsync(cts.Token);
}
catch (OperationCanceledException) { }

// Print summary of all sessions stored in the custom file store
fileStore.PrintSummary();

Console.WriteLine("\nShutting down...");

static void WriteColored(ConsoleColor color, string message)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}
