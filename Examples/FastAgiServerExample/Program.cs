// Asterisk.Sdk - FastAGI Server Example
// Demonstrates: start AGI server, register script handler, handle calls.

using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Microsoft.Extensions.Logging;

Console.WriteLine("Asterisk.Sdk - FastAGI Server Example");
Console.WriteLine("========================================");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

// 1. Create a mapping strategy that routes AGI requests to scripts
var mapping = new SimpleMappingStrategy();
mapping.Add("hello", new HelloScript());

// 2. Create and start the FastAGI server on port 4573
var server = new FastAgiServer(4573, mapping, loggerFactory.CreateLogger<FastAgiServer>());

Console.WriteLine("Starting FastAGI server on port 4573...");
await server.StartAsync();
Console.WriteLine("Server started. Configure Asterisk dialplan:");
Console.WriteLine("  exten => 100,1,AGI(agi://localhost/hello)");
Console.WriteLine("Press Ctrl+C to stop.");

// 3. Wait for shutdown signal
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
}
catch (OperationCanceledException) { }

// 4. Stop gracefully
await server.StopAsync();
Console.WriteLine("Server stopped.");

// Example AGI script that answers and plays a greeting
sealed class HelloScript : IAgiScript
{
    public async ValueTask ExecuteAsync(
        IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
    {
        await channel.AnswerAsync(cancellationToken);
        await channel.StreamFileAsync("hello-world", "", cancellationToken);
        await channel.HangupAsync(cancellationToken);
    }
}
