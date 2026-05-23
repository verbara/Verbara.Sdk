// AOT Canary — verifies all 16 SDK packages are AOT-safe (zero trim warnings).
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
using Microsoft.Extensions.DependencyInjection;

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



// v2.2.0+ Data.Npgsql + Cluster.Postgres force-load (ADR-0022 Phase D + Phase A.5).
// We reference public types without invoking I/O — connection-string-less constructors
// would block at runtime. Touching the types is enough to make the linker process the
// assemblies during dotnet publish.
_ = typeof(Verbara.Sdk.Data.Npgsql.NpgsqlExecutor);
_ = typeof(Verbara.Sdk.Cluster.Postgres.DependencyInjection.ClusterPostgresServiceCollectionExtensions);
_ = typeof(Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner);
