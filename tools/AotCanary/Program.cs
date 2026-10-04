// AOT Canary — publishes the SDK packages it references (23 of 29) with Native AOT; tools/verify-aot.sh
// fails on any trim/AOT warning and on any method the AOT compiler reports will always throw.
// References a representative public type from each package so the linker
// processes all assemblies during dotnet publish /p:PublishAot=true.

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

return 0;
