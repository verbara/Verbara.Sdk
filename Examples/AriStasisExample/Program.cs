// Asterisk.Sdk - ARI Stasis Application Example
// Demonstrates: connect to ARI WebSocket, subscribe to events, originate a call.

using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Microsoft.Extensions.Options;

Console.WriteLine("Asterisk.Sdk - ARI Stasis Example");
Console.WriteLine("====================================");

// 1. Create ARI client with options
var options = Options.Create(new AriClientOptions
{
    BaseUrl = "http://localhost:8088",
    Username = "asterisk",
    Password = "asterisk",
    Application = "hello-stasis"
});

using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8088/ari/") };
http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
    "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("asterisk:asterisk")));

var client = new AriClient(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AriClient>.Instance);

try
{
    // 2. Connect WebSocket for events
    Console.WriteLine("Connecting to ARI WebSocket...");
    await client.ConnectAsync();
    Console.WriteLine("Connected! Listening for Stasis events...");

    // 3. Subscribe to events
    using var sub = client.Subscribe(new AriEventPrinter());

    // 4. List existing channels
    var channels = await client.Channels.ListAsync();
    Console.WriteLine($"Active channels: {channels.Length}");

    // 5. Wait for events
    Console.WriteLine("Press Ctrl+C to stop.");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
}
finally
{
    await client.DisconnectAsync();
    Console.WriteLine("Disconnected.");
}

sealed class AriEventPrinter : IObserver<AriEvent>
{
    public void OnNext(AriEvent value) =>
        Console.WriteLine($"[ARI Event] {value.Type}: App={value.Application}");

    public void OnError(Exception error) =>
        Console.Error.WriteLine($"[ARI Error] {error.Message}");

    public void OnCompleted() =>
        Console.WriteLine("[ARI] Event stream ended.");
}
