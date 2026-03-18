// Asterisk.Sdk - PBX Activities Example
// Demonstrates: high-level Activities (PlayMessage, Dial, Queue, Hangup)
// on a FastAGI channel with status tracking.

using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Activities;
using Asterisk.Sdk.Activities.Models;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - PBX Activities Example");
Console.WriteLine("========================================");
Console.WriteLine("This example runs a FastAGI server that uses Activities");
Console.WriteLine("to control calls with status tracking.\n");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

// 1. Register AGI script that uses Activities
var mapping = new SimpleMappingStrategy();
mapping.Add("activities-demo", new ActivitiesDemoScript());

// 2. Start FastAGI server on port 4573
var server = new FastAgiServer(4573, mapping, loggerFactory.CreateLogger<FastAgiServer>());

Console.WriteLine("Starting FastAGI server on port 4573...");
await server.StartAsync();
Console.WriteLine("Server started. Configure Asterisk dialplan:");
Console.WriteLine("  exten => 200,1,AGI(agi://localhost:4573/activities-demo)");
Console.WriteLine("Press Ctrl+C to stop.\n");

// 3. Wait for shutdown signal
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }

await server.StopAsync();
Console.WriteLine("Server stopped.");

// --- AGI Script using Activities ---

/// <summary>
/// Demonstrates Activities on a live AGI channel:
/// 1. PlayMessageActivity — play a welcome prompt
/// 2. DialActivity — attempt to reach an agent
/// 3. QueueActivity — fallback to a queue if dial fails
/// 4. HangupActivity — clean hangup
/// Each activity tracks its Status through the lifecycle.
/// </summary>
sealed class ActivitiesDemoScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Activities] Incoming call from {request.CallerId} on {request.Channel}");

        await channel.AnswerAsync(cancellationToken);

        // --- Step 1: Play welcome message ---
        Console.WriteLine("[Activities] Step 1: Playing welcome message...");
        await using var playMessage = new PlayMessageActivity(channel)
        {
            FileName = "welcome"
        };
        await playMessage.StartAsync(cancellationToken);
        Console.WriteLine($"[Activities]   PlayMessage status: {playMessage.Status}");

        // --- Step 2: Dial an agent ---
        Console.WriteLine("[Activities] Step 2: Dialing agent at PJSIP/agent01...");
        await using var dial = new DialActivity(channel)
        {
            Target = new EndPoint(TechType.PJSIP, "agent01"),
            Timeout = TimeSpan.FromSeconds(20),
            Options = "t" // allow the called party to transfer
        };
        await dial.StartAsync(cancellationToken);
        Console.WriteLine($"[Activities]   Dial status: {dial.Status}, DIALSTATUS: {dial.DialStatus}");

        // --- Step 3: If not answered, route to queue ---
        if (dial.DialStatus is not "ANSWER")
        {
            Console.WriteLine($"[Activities] Step 3: Dial result was '{dial.DialStatus}', routing to support queue...");
            await using var queue = new QueueActivity(channel)
            {
                QueueName = "support",
                Options = "t",
                Timeout = TimeSpan.FromSeconds(120)
            };
            await queue.StartAsync(cancellationToken);
            Console.WriteLine($"[Activities]   Queue status: {queue.Status}, QUEUESTATUS: {queue.QueueStatus}");
        }
        else
        {
            Console.WriteLine("[Activities] Step 3: Agent answered, skipping queue.");
        }

        // --- Step 4: Hangup ---
        Console.WriteLine("[Activities] Step 4: Hanging up...");
        await using var hangup = new HangupActivity(channel);
        await hangup.StartAsync(cancellationToken);
        Console.WriteLine($"[Activities]   Hangup status: {hangup.Status}");

        Console.WriteLine("[Activities] Call flow complete.");
    }
}
