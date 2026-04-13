// AOT Canary — verifies all 16 SDK packages are AOT-safe (zero trim warnings).
// References a representative public type from each package so the linker
// processes all assemblies during dotnet publish /p:PublishAot=true.

using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Activities.Activities;
using Asterisk.Sdk.Config;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.Audio.Processing;
using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Stt.Deepgram;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using Asterisk.Sdk.VoiceAi.Testing;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Diagnostics;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

Console.WriteLine("AOT Canary — all SDK types are trim-safe");

// Asterisk.Sdk — core interfaces and enums
_ = typeof(IAmiConnection);

// Asterisk.Sdk.Ami — AMI protocol: actions, events, connection
_ = typeof(PingAction);
_ = typeof(HangupEvent);
_ = typeof(AmiConnection);
_ = typeof(AmiConnectionOptions);

// Asterisk.Sdk.Agi — FastAGI server
_ = typeof(FastAgiServer);
_ = typeof(AgiChannel);

// Asterisk.Sdk.Ari — ARI REST/WebSocket client
_ = typeof(AriClient);
_ = typeof(AriClientOptions);

// Asterisk.Sdk.Live — real-time domain objects
_ = typeof(AsteriskServer);
_ = typeof(AsteriskServerPool);

// Asterisk.Sdk.Activities — call activity state machines
_ = typeof(ActivityBase);

// Asterisk.Sdk.Config — .conf file parsers
_ = typeof(ConfigFileReader);
_ = typeof(ConfigFile);

// Asterisk.Sdk.Hosting — DI registration
_ = typeof(AsteriskOptions);
_ = typeof(AmiConnectionHostedService);

// Asterisk.Sdk.Sessions — session manager
_ = typeof(CallSession);
_ = typeof(CallSessionManager);

// Asterisk.Sdk.Audio — audio processing and resampling
_ = typeof(AudioEncoding);
_ = typeof(AudioProcessor);

// Asterisk.Sdk.VoiceAi — conversation pipeline
_ = typeof(ConversationContext);
_ = typeof(VoiceAiPipeline);

// Asterisk.Sdk.VoiceAi.AudioSocket — AudioSocket protocol
_ = typeof(AudioSocketClient);
_ = typeof(AudioSocketOptions);

// Asterisk.Sdk.VoiceAi.Stt — speech-to-text providers
_ = typeof(DeepgramSpeechRecognizer);
_ = typeof(DeepgramOptions);

// Asterisk.Sdk.VoiceAi.Tts — text-to-speech providers
_ = typeof(ElevenLabsSpeechSynthesizer);
_ = typeof(ElevenLabsOptions);

// Asterisk.Sdk.VoiceAi.Testing — fakes for unit testing
_ = typeof(FakeSpeechRecognizer);
_ = typeof(FakeConversationHandler);

// Asterisk.Sdk.VoiceAi.OpenAiRealtime — OpenAI Realtime bridge
_ = typeof(OpenAiRealtimeBridge);
_ = typeof(OpenAiRealtimeOptions);
_ = typeof(VadMode);

// Asterisk.Sdk.Push — in-memory push event bus + subscription registry + delivery filter
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

// Exercise AddAsteriskPush + publish/subscribe path to force linker analysis of runtime code.
var services = new ServiceCollection();
services.AddLogging();
services.AddAsteriskPush(o =>
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

using var sub = bus.OfType<Asterisk.Sdk.AotCanary.CanaryPushEvent>().Subscribe(static evt =>
    Console.WriteLine($"received: {evt.EventType} tenant={evt.Metadata.TenantId}"));

var sample = new Asterisk.Sdk.AotCanary.CanaryPushEvent
{
    Metadata = new PushEventMetadata("tenant-1", "user-1", DateTimeOffset.UtcNow, CorrelationId: null),
};
_ = filter.IsDeliverableToSubscriber(sample, subscriber);
await bus.PublishAsync(sample);

