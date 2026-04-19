// Asterisk.Sdk - Webhook Subscriber Example
// Demonstrates: outbound HTTP webhooks driven by Asterisk.Sdk.Push.
//
// Flow:
//   1. Start a tiny in-process HTTP listener to play the role of "webhook receiver".
//   2. Register a WebhookSubscription pointing at the listener with an HMAC secret.
//   3. Publish a sample PushEvent to the bus.
//   4. WebhookDeliveryService picks it up, POSTs to our listener with the X-Signature
//      header, and we verify the signature matches the expected HMAC-SHA256.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Topics;
using Asterisk.Sdk.Push.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebhookSubscriberExample;

const string secret = "demo-shared-secret";
const int listenerPort = 18080;
var receivedTcs = new TaskCompletionSource<(string body, string? signature)>();

// 1. Spin up a HttpListener playing the receiver role.
var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{listenerPort}/hook/");
listener.Start();
Console.WriteLine($"Webhook receiver listening at http://localhost:{listenerPort}/hook/");

_ = Task.Run(async () =>
{
    var ctx = await listener.GetContextAsync();
    using var reader = new StreamReader(ctx.Request.InputStream);
    var body = await reader.ReadToEndAsync();
    var sig = ctx.Request.Headers["X-Signature"];

    ctx.Response.StatusCode = (int)HttpStatusCode.OK;
    ctx.Response.Close();

    receivedTcs.TrySetResult((body, sig));
});

// 2. Build the Push + Webhooks host.
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders().AddConsole();
builder.Services.AddAsteriskPush();
builder.Services.AddAsteriskPushWebhooks(opts =>
{
    opts.MaxRetries = 2;
    opts.InitialDelay = TimeSpan.FromMilliseconds(250);
});

using var host = builder.Build();
await host.StartAsync();

// 3. Register a subscription targeting the local listener.
var store = host.Services.GetRequiredService<IWebhookSubscriptionStore>();
await store.AddAsync(new WebhookSubscription
{
    Id = "demo",
    TopicPattern = TopicPattern.Parse("calls.**"),
    TargetUrl = new Uri($"http://localhost:{listenerPort}/hook/"),
    Secret = secret,
});
Console.WriteLine("Subscription registered: topic=calls.**  target=/hook/");

// 4. Publish a sample event. Its TopicPath must match the subscription pattern.
var bus = host.Services.GetRequiredService<IPushEventBus>();
var sampleEvent = new DemoCallStartedEvent
{
    Metadata = new PushEventMetadata(
        TenantId: "tenant-demo",
        UserId: "agent-007",
        OccurredAt: DateTimeOffset.UtcNow,
        CorrelationId: Guid.NewGuid().ToString("N"),
        TopicPath: "calls.inbound.started"),
};
await bus.PublishAsync(sampleEvent);
Console.WriteLine($"Published event: {sampleEvent.EventType}  topic={sampleEvent.Metadata.TopicPath}");

// 5. Wait for the receiver.
var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine();
Console.WriteLine("=== webhook received ===");
Console.WriteLine($"X-Signature: {received.signature}");
Console.WriteLine($"Body: {received.body}");
Console.WriteLine();

// 6. Verify the signature ourselves (what a real receiver would do).
var expected = "sha256=" + Convert.ToHexString(
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(received.body))).ToLowerInvariant();
Console.WriteLine($"Expected signature: {expected}");
Console.WriteLine($"Signature match: {expected == received.signature}");

await host.StopAsync();
listener.Stop();

