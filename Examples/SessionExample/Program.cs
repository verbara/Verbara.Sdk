// Asterisk.Sdk - Session Example
// Demonstrates: real-time session monitoring with domain events and periodic summaries.

using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.WriteLine("Asterisk.Sdk - Session Monitor Example");
Console.WriteLine("=========================================");

// 1. Build host with Asterisk + Sessions
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAsterisk(options =>
{
    options.Ami.Hostname = builder.Configuration["Asterisk:Hostname"] ?? "localhost";
    options.Ami.Username = builder.Configuration["Asterisk:Username"] ?? "admin";
    options.Ami.Password = builder.Configuration["Asterisk:Password"] ?? "secret";
});

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
            var session = sessionManager.GetById(connected.SessionId);
            var participants = session is not null
                ? $"  Participants: {session.Participants.Count}"
                : "";
            WriteColored(ConsoleColor.Cyan,
                $"[{ts}] CONNECT  {id}  Agent: {connected.AgentId ?? "-"}  " +
                $"Queue: {connected.QueueName ?? "-"}  Wait: {connected.WaitTime.TotalSeconds:F1}s{participants}");
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

// 3. Periodic summary
using var summaryTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("Monitoring sessions (press Ctrl+C to stop)...\n");

// Start the host in the background
_ = app.RunAsync(cts.Token);

// Print summaries until cancelled
try
{
    while (await summaryTimer.WaitForNextTickAsync(cts.Token))
    {
        var active = sessionManager.ActiveSessions.Count();
        var recent = sessionManager.GetRecentCompleted(10).Count();
        WriteColored(ConsoleColor.DarkYellow,
            $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] --- Active: {active}  |  Recent completed: {recent} ---");
    }
}
catch (OperationCanceledException) { }

Console.WriteLine("\nShutting down...");

static void WriteColored(ConsoleColor color, string message)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}
