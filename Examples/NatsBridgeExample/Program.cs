// Asterisk.Sdk — Push.Nats bridge example.
//
// Shows Asterisk.Sdk.Push.Nats fanning PushEvent instances out to a NATS
// cluster. The topic hierarchy (calls/agents/queues/etc.) maps to a NATS
// subject tree rooted at the configured prefix (default "asterisk.sdk"),
// so a separate subscriber — in another process, on another host — can
// filter by subject wildcard and receive events in near-real-time.
//
// This example also runs an inline NATS subscriber in the same process
// so you can see the end-to-end flow without spinning up a second app.
//
// Prerequisites:
//   docker run -p 4222:4222 nats:2.10-alpine
// (or any reachable NATS server at nats://127.0.0.1:4222)

using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Nats;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsBridgeExample;

const string natsUrl = "nats://127.0.0.1:4222";
const string subjectPrefix = "asterisk.sdk";

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders().AddConsole();
builder.Services.AddAsteriskPush();
builder.Services.AddPushNats(opt =>
{
    opt.Url = natsUrl;
    opt.SubjectPrefix = subjectPrefix;
});

using var host = builder.Build();
await host.StartAsync();
Console.WriteLine($"Push.Nats bridge started → {natsUrl} (prefix: {subjectPrefix})");

// Inline NATS subscriber in the same process — in production this would
// typically run on another host / service.
await using var natsConn = new NatsConnection(new NatsOpts { Url = natsUrl });
await natsConn.ConnectAsync();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

_ = Task.Run(async () =>
{
    await foreach (var msg in natsConn.SubscribeAsync<byte[]>($"{subjectPrefix}.>", cancellationToken: cts.Token))
    {
        var body = System.Text.Encoding.UTF8.GetString(msg.Data ?? []);
        Console.WriteLine($"[nats] subject={msg.Subject} body={body}");
    }
}, cts.Token);

// Give the subscription a moment to establish.
await Task.Delay(300, cts.Token);

var bus = host.Services.GetRequiredService<IPushEventBus>();
foreach (var topic in new[] { "calls.inbound.started", "agents.42.state", "queues.sales.wait" })
{
    var evt = new DemoBridgeEvent
    {
        Metadata = new PushEventMetadata(
            TenantId: "tenant-demo",
            UserId: "agent-007",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid().ToString("N"),
            TopicPath: topic),
    };
    await bus.PublishAsync(evt, cts.Token);
    Console.WriteLine($"[push] published topic={topic}");
}

Console.WriteLine("Waiting for NATS echoes (Ctrl+C to stop)...");
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }

await host.StopAsync();
Console.WriteLine("Host stopped.");
