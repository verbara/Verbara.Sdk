// Asterisk.Sdk — ARI Outbound WebSocket listener example.
//
// Shows how to act as the WebSocket *server* that Asterisk 22.5+ dials
// into (reverse of the classic inbound ARI pattern). The listener
// validates the upgrade path, optional Basic-Auth credentials, and an
// allow-list of Stasis application names, then exposes each accepted
// connection as an AriOutboundConnection with a typed IObservable<AriEvent>.
//
// Asterisk-side config (ari.conf + res_websocket_client.so):
//   [outbound-app]
//   type = user
//   read_only = yes
//   password = s3cret
//
//   application = outbound
//   websocket_client_id = outbound-app-client
//
// Dialplan:
//   exten => 7000,1,Answer()
//    same => n,Stasis(outbound-app)
//    same => n,Hangup()

using Asterisk.Sdk.Ari.Outbound;
using Asterisk.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders().AddConsole();

builder.Services.AddAriOutboundListener(opt =>
{
    opt.Port = 8099;
    opt.ListenAddress = "0.0.0.0";
    opt.Path = "/ari/events";
    opt.ExpectedUsername = "outbound-app";
    opt.ExpectedPassword = "s3cret";
    opt.AllowedApplications.Add("outbound-app");
});

using var host = builder.Build();
await host.StartAsync();

var listener = host.Services.GetRequiredService<IAriOutboundListener>();

using var connSub = listener.OnConnectionAccepted.Subscribe(conn =>
{
    Console.WriteLine($"[conn] accepted application={conn.ApplicationName} remote={conn.RemoteEndpoint}");

    _ = conn.Events.Subscribe(
        evt => Console.WriteLine($"[event] {evt.Type} application={evt.Application}"),
        ex => Console.Error.WriteLine($"[event] error: {ex.Message}"),
        () => Console.WriteLine($"[event] stream ended for {conn.ApplicationName}"));
});

Console.WriteLine("ARI Outbound listener waiting on http://0.0.0.0:8099/ari/events");
Console.WriteLine("Configure Asterisk ari.conf + res_websocket_client and trigger Stasis(outbound-app) to connect.");
Console.WriteLine($"Active connections: {listener.ActiveConnectionCount}");
Console.WriteLine("Press Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }

await host.StopAsync();
Console.WriteLine("Host stopped.");
