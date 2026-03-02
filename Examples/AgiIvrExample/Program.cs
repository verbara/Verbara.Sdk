// Asterisk.Sdk - AGI IVR Example
// Demonstrates: complex IVR script with Answer, GetData, conditional routing,
// Dial/VoiceMail, and Hangup using SimpleMappingStrategy.

using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - AGI IVR Example");
Console.WriteLine("================================");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// 1. Create mapping strategy with multiple script routes
var mapping = new SimpleMappingStrategy();
mapping.Add("ivr-main", new MainIvrScript());
mapping.Add("ivr-support", new SupportScript());
mapping.Add("ivr-sales", new SalesScript());

// 2. Start FastAGI server
var port = 4573;
var server = new FastAgiServer(port, mapping, loggerFactory.CreateLogger<FastAgiServer>());

Console.WriteLine($"Starting FastAGI server on port {port}...");
await server.StartAsync();
Console.WriteLine("Server started. Configure Asterisk dialplan:");
Console.WriteLine($"  exten => 100,1,AGI(agi://localhost:{port}/ivr-main)");
Console.WriteLine($"  exten => 101,1,AGI(agi://localhost:{port}/ivr-support)");
Console.WriteLine($"  exten => 102,1,AGI(agi://localhost:{port}/ivr-sales)");
Console.WriteLine("Press Ctrl+C to stop.");

// 3. Wait for shutdown
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }

await server.StopAsync();
Console.WriteLine("Server stopped.");

// --- IVR Script Implementations ---

/// <summary>
/// Main IVR menu: greets the caller, collects DTMF input, and routes accordingly.
/// Press 1 for Support, 2 for Sales, * to repeat.
/// </summary>
sealed class MainIvrScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[IVR] Call from {request.CallerId} ({request.CallerIdName}) on {request.Channel}");

        await channel.AnswerAsync(cancellationToken);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Play menu and collect 1 digit (timeout 5 seconds)
            var digits = await channel.GetDataAsync("ivr-menu", timeout: 5000, maxDigits: 1, cancellationToken: cancellationToken);

            switch (digits)
            {
                case "1":
                    Console.WriteLine("[IVR] Routing to Support queue...");
                    await channel.ExecAsync("Queue", "support,t", cancellationToken);
                    await channel.HangupAsync(cancellationToken);
                    return;

                case "2":
                    Console.WriteLine("[IVR] Routing to Sales queue...");
                    await channel.ExecAsync("Queue", "sales,t", cancellationToken);
                    await channel.HangupAsync(cancellationToken);
                    return;

                case "*":
                    Console.WriteLine("[IVR] Repeating menu...");
                    continue;

                default:
                    // Invalid or no input — play error and retry
                    await channel.StreamFileAsync("invalid-option", "", cancellationToken);
                    break;
            }
        }

        // Max attempts exceeded — route to voicemail
        Console.WriteLine("[IVR] Max attempts. Routing to voicemail...");
        await channel.ExecAsync("VoiceMail", "100@default", cancellationToken);
        await channel.HangupAsync(cancellationToken);
    }
}

/// <summary>Support script: answers and plays a support greeting.</summary>
sealed class SupportScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
    {
        await channel.AnswerAsync(cancellationToken);
        Console.WriteLine($"[Support] Handling call from {request.CallerId}");

        // Set channel variable for tracking
        await channel.SetVariableAsync("DEPARTMENT", "support", cancellationToken);
        var dept = await channel.GetVariableAsync("DEPARTMENT", cancellationToken);
        Console.WriteLine($"[Support] Department variable: {dept}");

        await channel.StreamFileAsync("support-greeting", "", cancellationToken);
        await channel.ExecAsync("Queue", "support,t", cancellationToken);
        await channel.HangupAsync(cancellationToken);
    }
}

/// <summary>Sales script: answers and routes to the sales queue.</summary>
sealed class SalesScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
    {
        await channel.AnswerAsync(cancellationToken);
        Console.WriteLine($"[Sales] Handling call from {request.CallerId}");

        await channel.StreamFileAsync("sales-greeting", "", cancellationToken);
        await channel.ExecAsync("Dial", "PJSIP/3000,30,t", cancellationToken);
        await channel.HangupAsync(cancellationToken);
    }
}
