# Examples

Standalone .NET console apps demonstrating each layer of the Verbara SDK.
Every example is a self-contained project you can build and run individually.

## Prerequisites

- .NET 10 SDK (10.0.100+)
- Asterisk 18+ for examples that connect to a live PBX (AMI, AGI, ARI, Live, Sessions)
- Provider API keys for VoiceAi examples (AssemblyAI, Cartesia, Speechmatics, OpenAI)
- Docker for Redis/PostgreSQL session store examples
- NATS server for the NatsBridge example

Run any example with:

```bash
dotnet run --project Examples/{ExampleName}/
```

## Example Catalog

### AMI (Asterisk Manager Interface)

| Example | Description |
|---------|-------------|
| [BasicAmiExample](BasicAmiExample/) | Basic AMI connection, send actions, subscribe to events |
| [AmiAdvancedExample](AmiAdvancedExample/) | Advanced AMI patterns: originate calls, redirect channels, queue management |

### AGI (Asterisk Gateway Interface)

| Example | Description |
|---------|-------------|
| [FastAgiServerExample](FastAgiServerExample/) | FastAGI server with pluggable script handlers |
| [AgiIvrExample](AgiIvrExample/) | Interactive Voice Response (IVR) menu via AGI |

### ARI (Asterisk REST Interface)

| Example | Description |
|---------|-------------|
| [AriStasisExample](AriStasisExample/) | ARI WebSocket connection and Stasis event handling |
| [AriChannelControlExample](AriChannelControlExample/) | ARI channel origination and bridge management |
| [AriOutboundExample](AriOutboundExample/) | ARI outbound WebSocket listener (Asterisk connects to your app) |
| [WebSocketMediaExample](WebSocketMediaExample/) | chan_websocket audio streaming and JSON control protocol (Asterisk 22.8+/23.2+) |

### Live API

| Example | Description |
|---------|-------------|
| [LiveApiExample](LiveApiExample/) | Real-time channel and queue tracking via the Live API |

### Sessions

| Example | Description |
|---------|-------------|
| [SessionExample](SessionExample/) | Session Engine: call session correlation and domain events |
| [SessionExtensionsExample](SessionExtensionsExample/) | Session Engine extension points: enrichers, policies, event handlers |
| [SessionsRedisExample](SessionsRedisExample/) | Redis-backed session store (requires Docker) |
| [SessionsPostgresExample](SessionsPostgresExample/) | PostgreSQL-backed session store (requires Docker) |

### Push (Event Distribution)

| Example | Description |
|---------|-------------|
| [NatsBridgeExample](NatsBridgeExample/) | Push.Nats bridge for cross-service event distribution |
| [WebhookSubscriberExample](WebhookSubscriberExample/) | Webhook subscriber with HMAC-signed delivery |

### VoiceAi

| Example | Description |
|---------|-------------|
| [VoiceAiExample](VoiceAiExample/) | Turn-based Voice AI pipeline: STT + TTS + echo handler |
| [VoiceAiAssemblyAiExample](VoiceAiAssemblyAiExample/) | Voice AI with AssemblyAI STT provider |
| [VoiceAiCartesiaExample](VoiceAiCartesiaExample/) | Voice AI with Cartesia Sonic TTS provider |
| [VoiceAiSpeechmaticsExample](VoiceAiSpeechmaticsExample/) | Voice AI with Speechmatics STT provider |
| [VoiceAiCustomProviderExample](VoiceAiCustomProviderExample/) | Custom VoiceAi provider implementation pattern |
| [OpenAiRealtimeExample](OpenAiRealtimeExample/) | GPT-4o direct bridge via OpenAI Realtime API with function calling |

### Telemetry

| Example | Description |
|---------|-------------|
| [TelemetryExample](TelemetryExample/) | OpenTelemetry discovery: ActivitySources, Meters, HealthChecks |

### Multi-Server

| Example | Description |
|---------|-------------|
| [MultiServerExample](MultiServerExample/) | Federated multi-server management with agent routing |

### Activities (High-Level Telephony)

| Example | Description |
|---------|-------------|
| [PbxActivitiesExample](PbxActivitiesExample/) | High-level telephony activities: Dial, Hold, Transfer with status tracking |
| [ContactCenterSupervisionExample](ContactCenterSupervisionExample/) | Contact center supervision: ChanSpy, Barge, Snoop, Attended Transfer |

### PbxAdmin

The PbxAdmin Blazor application has been moved to its own repository:
[github.com/verbara/Verbara.Sdk.PbxAdmin](https://github.com/verbara/Verbara.Sdk.PbxAdmin)
