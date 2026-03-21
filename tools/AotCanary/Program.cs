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
