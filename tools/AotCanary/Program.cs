// AOT Canary — references the 28 runtime SDK packages (the 29th under src/, the source generator, is an analyzer and
// never reaches an image) and publishes them with Native AOT; tools/verify-aot.sh fails on any trim/AOT warning and on
// any method the AOT compiler reports will always throw.
// ILC analyzes only the code the canary calls: a package referenced through a typeof alone has none of its methods
// compiled, so a trim hazard inside it is not seen here and rests on the package's own IsAotCompatible analyzers.

using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Config;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Audio.Processing;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.Pipeline;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using Verbara.Sdk.VoiceAi.Testing;
using Verbara.Sdk.VoiceAi.OpenAiRealtime;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Subscriptions;
using Verbara.Sdk.Push.Webhooks;
using Verbara.Sdk.Resilience;
using Verbara.Sdk.Cluster.Primitives;
using Verbara.Sdk.Cluster.Primitives.InMemory;
using Verbara.Sdk.Push.AspNetCore;
using Verbara.Sdk.Push.Nats;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Postgres;
using Verbara.Sdk.Sessions.Redis;
using Verbara.Sdk.VoiceAi.TurnDetection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

Console.WriteLine("AOT Canary — all SDK types are trim-safe");

// Verbara.Sdk — core interfaces and enums
_ = typeof(IAmiConnection);

// Verbara.Sdk.Ami — AMI protocol: actions, events, connection
_ = typeof(PingAction);
_ = typeof(HangupEvent);
_ = typeof(AmiConnection);
_ = typeof(AmiConnectionOptions);

// Verbara.Sdk.Agi — FastAGI server
_ = typeof(FastAgiServer);
_ = typeof(AgiChannel);

// Verbara.Sdk.Ari — ARI REST/WebSocket client
_ = typeof(AriClient);
_ = typeof(AriClientOptions);

// Verbara.Sdk.Live — real-time domain objects
_ = typeof(VerbaraServer);
_ = typeof(VerbaraServerPool);

// Verbara.Sdk.Activities — call activity state machines
_ = typeof(ActivityBase);

// Verbara.Sdk.Config — .conf file parsers
_ = typeof(ConfigFileReader);
_ = typeof(ConfigFile);

// Verbara.Sdk.Hosting — DI registration
_ = typeof(VerbaraOptions);
_ = typeof(AmiConnectionHostedService);

// Verbara.Sdk.Sessions — session manager
_ = typeof(CallSession);
_ = typeof(CallSessionManager);

// Verbara.Sdk.Audio — audio processing and resampling
_ = typeof(AudioEncoding);
_ = typeof(AudioProcessor);

// Verbara.Sdk.VoiceAi — conversation pipeline
_ = typeof(ConversationContext);
_ = typeof(VoiceAiPipeline);

// Verbara.Sdk.VoiceAi.AudioSocket — AudioSocket protocol
_ = typeof(AudioSocketClient);
_ = typeof(AudioSocketOptions);

// Verbara.Sdk.VoiceAi.Stt — speech-to-text providers
_ = typeof(DeepgramSpeechRecognizer);
_ = typeof(DeepgramOptions);

// Verbara.Sdk.VoiceAi.Tts — text-to-speech providers
_ = typeof(ElevenLabsSpeechSynthesizer);
_ = typeof(ElevenLabsOptions);

// Verbara.Sdk.VoiceAi.Testing — fakes for unit testing
_ = typeof(FakeSpeechRecognizer);
_ = typeof(FakeConversationHandler);

// Verbara.Sdk.VoiceAi.OpenAiRealtime — OpenAI Realtime bridge
_ = typeof(OpenAiRealtimeBridge);
_ = typeof(OpenAiRealtimeOptions);
_ = typeof(VadMode);

// Verbara.Sdk.Push — in-memory push event bus + subscription registry + delivery filter
_ = typeof(IPushEventBus);
_ = typeof(RxPushEventBus);
_ = typeof(PushEventBusOptions);
_ = typeof(BackpressureStrategy);
_ = typeof(PushEvent);
_ = typeof(PushEventMetadata);
_ = typeof(SubscriberContext);
_ = typeof(IEventDeliveryFilter);
_ = typeof(DefaultDeliveryFilter);
_ = typeof(ISubscriptionRegistry);
_ = typeof(InMemorySubscriptionRegistry);
_ = typeof(PushMetrics);

// Verbara.Sdk.Push.Webhooks — outbound webhook delivery with HMAC + circuit breaker
_ = typeof(WebhookSubscription);
_ = typeof(WebhookDeliveryOptions);
_ = typeof(WebhookDeliveryService);
_ = typeof(HmacSha256Signer);

// Verbara.Sdk.Resilience — composable circuit breaker + retry + timeout primitives
_ = typeof(CircuitBreakerState);
_ = typeof(ResiliencePolicy);
_ = typeof(ResiliencePolicyBuilder);
_ = typeof(BackoffSchedule);
_ = typeof(CircuitBreakerOpenException);

// Verbara.Sdk.Cluster.Primitives — cluster transport / membership / lock abstractions
_ = typeof(ClusterEvent);
_ = typeof(NodeInfo);
_ = typeof(NodeState);
_ = typeof(IClusterTransport);
_ = typeof(IDistributedLock);
_ = typeof(IMembershipProvider);
_ = typeof(InMemoryClusterTransport);
_ = typeof(InMemoryDistributedLock);
_ = typeof(InMemoryMembershipProvider);

// Exercise AddVerbaraPush + publish/subscribe path to force linker analysis of runtime code.
var services = new ServiceCollection();
services.AddLogging();
services.AddVerbaraPush(o =>
{
    o.BufferCapacity = 64;
    o.BackpressureStrategy = BackpressureStrategy.DropOldest;
});
using var sp = services.BuildServiceProvider();
var bus = sp.GetRequiredService<IPushEventBus>();
var filter = sp.GetRequiredService<IEventDeliveryFilter>();
var registry = sp.GetRequiredService<ISubscriptionRegistry>();

var subscriber = new SubscriberContext(
    TenantId: "tenant-1",
    UserId: "user-1",
    Roles: new HashSet<string> { "agent" },
    Permissions: new HashSet<string> { "conversation:read" });
using var _registration = registry.Register(subscriber);

using var sub = bus.OfType<Verbara.Sdk.AotCanary.CanaryPushEvent>().Subscribe(static evt =>
    Console.WriteLine($"received: {evt.EventType} tenant={evt.Metadata.TenantId}"));

var sample = new Verbara.Sdk.AotCanary.CanaryPushEvent
{
    Metadata = new PushEventMetadata("tenant-1", "user-1", DateTimeOffset.UtcNow, CorrelationId: null),
};
_ = filter.IsDeliverableToSubscriber(sample, subscriber);
await bus.PublishAsync(sample);

// Exercise AddVerbara(IConfiguration): the Asterisk:Ami / Asterisk:Ari sections are bound by the configuration-binding
// source generator, and only a call brings that generated code into ILC's analysis (a typeof would not). The bound
// values are printed and checked, so the smoke run fails if the native binary stops reading them.
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Asterisk:Ami:Username"] = "canary",
            ["Asterisk:Ami:Password"] = "canary",
            ["Asterisk:Ami:MaxReconnectAttempts"] = "3",
            ["Asterisk:Ami:ReconnectMultiplier"] = "1.5",
            ["Asterisk:Ami:ReconnectInitialDelay"] = "00:00:03",
            ["Asterisk:Ari:Username"] = "canary",
            ["Asterisk:Ari:Password"] = "canary",
            ["Asterisk:Ari:Application"] = "canary",
            ["Asterisk:Ari:AutoReconnect"] = "false",
            ["Asterisk:AgiPort"] = "4999",
        })
        .Build();
    var hostingServices = new ServiceCollection();
    hostingServices.AddLogging();
    hostingServices.AddVerbara(configuration);
    using var hostingProvider = hostingServices.BuildServiceProvider();
    var ami = hostingProvider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value;
    var ari = hostingProvider.GetRequiredService<IOptions<AriClientOptions>>().Value;
    Console.WriteLine(
        $"AddVerbara(IConfiguration): MaxReconnectAttempts={ami.MaxReconnectAttempts} ReconnectMultiplier={ami.ReconnectMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"ReconnectInitialDelay={ami.ReconnectInitialDelay:c} Ari.AutoReconnect={ari.AutoReconnect}");
    if (ami.MaxReconnectAttempts != 3 || ami.ReconnectMultiplier != 1.5 || ami.ReconnectInitialDelay != TimeSpan.FromSeconds(3) || ari.AutoReconnect)
    {
        Console.Error.WriteLine("AOT canary: AddVerbara(IConfiguration) did not deliver the configured values.");
        return 1;
    }
}

// Exercise the multi-server sessions registration: its pool sweep is a hosted service built by an explicit factory, and
// only resolving and starting it brings that factory and the sweep's loop into ILC's analysis.
{
    var poolServices = new ServiceCollection();
    poolServices.AddLogging();
    poolServices.AddVerbaraMultiServer();
    poolServices.AddVerbaraSessionsMultiServer();
    await using var poolProvider = poolServices.BuildServiceProvider();
    var hosted = poolProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
    foreach (var service in hosted)
        await service.StartAsync(CancellationToken.None);
    foreach (var service in hosted)
        await service.StopAsync(CancellationToken.None);
    var names = string.Join(",", hosted.Select(service => service.GetType().Name).Order(StringComparer.Ordinal));
    Console.WriteLine($"AddVerbaraSessionsMultiServer: hosted={names}");
    if (!names.Contains("PoolReconciliationService", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("AOT canary: AddVerbaraSessionsMultiServer registered no pool sweep.");
        return 1;
    }
}



// v2.2.0+ Data.Npgsql + Cluster.Postgres force-load (Platform/ADR-0022 Phase D + Phase A.5).
// We reference public types without invoking I/O — connection-string-less constructors
// would block at runtime. A typeof keeps the type in the image but does not bring its
// methods into ILC's analysis: only code this program calls is checked for trim and AOT
// warnings here. The compile-time analyzers (IsAotCompatible) are what check the rest.
_ = typeof(Verbara.Sdk.Data.Npgsql.NpgsqlExecutor);
_ = typeof(Verbara.Sdk.Cluster.Postgres.DependencyInjection.ClusterPostgresServiceCollectionExtensions);
_ = typeof(Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner);

// Verbara.Sdk.OpenTelemetry — call the registration (not a typeof) so ILC compiles the exporters it
// registers, the Prometheus serializer included. A dependency drift that leaves one of their methods
// unable to run then shows up as an ILC "will always throw" line, which tools/verify-aot.sh fails on.
{
    var otelServices = new ServiceCollection();
    Verbara.Sdk.OpenTelemetry.VerbaraOpenTelemetryExtensions.AddVerbaraOpenTelemetry(
        otelServices, b => b.WithAllSources().WithPrometheusExporter());
    using var otelProvider = otelServices.BuildServiceProvider();
    Console.WriteLine("OpenTelemetry: Prometheus exporter registered");
}

// Paths that need a live NATS, Redis or Postgres run only when AOT_CANARY_LIVE=1. ILC cannot fold an environment
// read, so it compiles them either way, and the smoke run stays offline.
var live = Environment.GetEnvironmentVariable("AOT_CANARY_LIVE") == "1";

// Verbara.Sdk.Push.Nats — register the bridge, resolve it, and round-trip an event through the default payload
// serializer and deserializer; connecting is the live path.
{
    var natsServices = new ServiceCollection();
    natsServices.AddLogging();
    natsServices.AddVerbaraPush();
    natsServices.AddPushNats(o =>
    {
        o.Url = "nats://127.0.0.1:4222";
        o.NodeId = "canary";
    });
    await using var natsProvider = natsServices.BuildServiceProvider();
    var natsPayload = natsProvider.GetRequiredService<INatsPayloadSerializer>().Serialize(sample);
    var natsSubject = NatsSubjectTranslator.ToNatsSubject("canary/ping", "verbara.push");
    var remote = natsProvider.GetRequiredService<INatsPayloadDeserializer>().Deserialize(natsSubject, natsPayload);
    var bridge = natsProvider.GetRequiredService<NatsBridge>();
    if (live)
    {
        await bridge.StartAsync(CancellationToken.None);
        await bridge.StopAsync(CancellationToken.None);
    }
    Console.WriteLine($"Push.Nats: subject={natsSubject} payloadBytes={natsPayload.Length} remote={remote?.EventType ?? "<null>"}");
    if (natsPayload.Length == 0 || remote is null)
    {
        Console.Error.WriteLine("AOT canary: the NATS payload did not round-trip.");
        return 1;
    }
}

// Verbara.Sdk.Sessions.Postgres / Verbara.Sdk.Sessions.Redis — register each backend and resolve the store it puts in
// place of the in-memory default. A Postgres data source does not connect until used, so its store is resolved offline;
// the Redis store's multiplexer connects on resolve, so that store and both stores' I/O are the live path.
{
    var pgServices = new ServiceCollection();
    pgServices.AddLogging();
    pgServices.AddVerbaraSessionsBuilder().UsePostgres("Host=127.0.0.1;Database=canary;Username=canary;Password=canary");
    await using var pgProvider = pgServices.BuildServiceProvider();
    var pgStore = pgProvider.GetRequiredService<SessionStoreBase>();
    Console.WriteLine($"Sessions.Postgres: store={pgStore.GetType().Name}");
    if (pgStore is not PostgresSessionStore)
    {
        Console.Error.WriteLine("AOT canary: UsePostgres did not replace the session store.");
        return 1;
    }

    var redisServices = new ServiceCollection();
    redisServices.AddLogging();
    redisServices.AddVerbaraSessionsBuilder().UseRedis("127.0.0.1:6379");
    await using var redisProvider = redisServices.BuildServiceProvider();
    var redisDescriptor = redisServices.Last(d => d.ServiceType == typeof(SessionStoreBase));
    Console.WriteLine($"Sessions.Redis: store={redisDescriptor.ImplementationType?.Name}");
    if (redisDescriptor.ImplementationType != typeof(RedisSessionStore))
    {
        Console.Error.WriteLine("AOT canary: UseRedis did not replace the session store.");
        return 1;
    }

    if (live)
    {
        var session = new CallSession("canary-session", "canary-linked", "canary-server", CallDirection.Inbound);
        foreach (var store in new[] { pgStore, redisProvider.GetRequiredService<SessionStoreBase>() })
        {
            await store.SaveAsync(session, CancellationToken.None);
            _ = await store.GetAsync(session.SessionId, CancellationToken.None);
            _ = await store.GetActiveAsync(CancellationToken.None);
            await store.DeleteAsync(session.SessionId, CancellationToken.None);
        }
    }
}

// Verbara.Sdk.Push.AspNetCore — register the SSE stream and map its endpoint on a host that is built but never run.
{
    var webBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
    webBuilder.Services.AddVerbaraPushAspNetCore();
    await using var web = webBuilder.Build();
    web.MapPushEndpoints();
    var endpoints = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)web).DataSources.SelectMany(d => d.Endpoints).ToList();
    Console.WriteLine($"Push.AspNetCore: endpoints={string.Join(",", endpoints.Select(e => e.DisplayName))}");
    if (endpoints.Count == 0)
    {
        Console.Error.WriteLine("AOT canary: MapPushEndpoints mapped no endpoint.");
        return 1;
    }
}

// Verbara.Sdk.VoiceAi.TurnDetection — resolve the detector, which loads the embedded ONNX model into the native ONNX
// runtime published beside the binary, then feed it one second of tone and 300 ms of silence: the 200 ms silence
// trigger runs the model once on the buffered speech, so the smoke run proves inference works in the native image.
{
    var turnServices = new ServiceCollection();
    turnServices.AddLogging();
    turnServices.AddSmartTurnDetection();
    await using var turnProvider = turnServices.BuildServiceProvider();
    var detector = turnProvider.GetRequiredService<ITurnDetector>();
    if (detector is not SmartTurnDetector)
    {
        Console.Error.WriteLine("AOT canary: AddSmartTurnDetection did not register the smart detector.");
        return 1;
    }

    var tone = new short[160];
    for (var i = 0; i < tone.Length; i++)
        tone[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));
    var silence = new short[160];
    var signals = new List<string>();
    for (var frame = 0; frame < 50; frame++)
        signals.Add(detector.Analyze(tone, isAssistantSpeaking: false).Action.ToString());
    for (var frame = 0; frame < 15; frame++)
        signals.Add(detector.Analyze(silence, isAssistantSpeaking: false).Action.ToString());
    var changes = string.Join(",", signals.Where(a => a != nameof(TurnAction.Continue)));
    Console.WriteLine($"TurnDetection: detector={detector.GetType().Name} signals={changes}");
    if (!signals.Contains(nameof(TurnAction.SpeechStarted)))
    {
        Console.Error.WriteLine("AOT canary: the smart detector did not see the tone as speech.");
        return 1;
    }
    if (!signals.Contains(nameof(TurnAction.EndOfUtterance)))
    {
        Console.Error.WriteLine("AOT canary: the smart detector's ONNX inference did not end the utterance on silence.");
        return 1;
    }
}

return 0;
