# Asterisk.Sdk

## The Modern .NET SDK for Asterisk PBX

> Build telephony applications on your own infrastructure. Free. Open source. Production-ready.


## The Problem

Building on Asterisk with .NET today means choosing between abandoned libraries or writing everything from scratch. AsterNET was last updated in 2018 and targets .NET Framework 4.0. Asterisk.NET has been dormant since 2013. The only actively maintained alternative covers AMI alone -- no ARI, no Live objects, no Session Engine, no Voice AI pipeline. If your stack is .NET and your PBX is Asterisk, the ecosystem has left you behind.

Meanwhile, cloud telephony platforms like Twilio and Vonage charge per minute and lock you into their infrastructure. They work well for simple use cases, but the moment you need custom call flows, on-premise deployment, or full control over your voice data, you hit walls -- and bills. If you want Asterisk's flexibility with .NET's ecosystem, you have been on your own. Until now.


## The Solution

Asterisk.Sdk is the first complete .NET SDK for Asterisk. One NuGet install gives you all three Asterisk interfaces (AMI, AGI, ARI), plus a real-time Live API, a Session Engine for call correlation, and a full Voice AI stack with pluggable speech-to-text and text-to-speech providers. Everything is built for .NET 10 with Native AOT -- zero runtime reflection, zero trim warnings.

The SDK is ported from asterisk-java, the most mature Asterisk library in any language, with over 2,470 commits across its 3.42.0 release history and 449 GitHub stars. But this is not a line-for-line translation. It is a complete redesign using System.IO.Pipelines for zero-copy TCP parsing, System.Threading.Channels for backpressure-aware event dispatch, System.Reactive for real-time state machines, and four custom source generators for full AOT compliance. The result is a library that inherits two decades of protocol knowledge while taking full advantage of modern .NET.


## What You Get

**Complete Asterisk Integration** -- Connect to any Asterisk server via AMI (Manager Interface), run custom call logic via AGI (Gateway Interface), and manage channels, bridges, and recordings via ARI (REST Interface). One SDK covers all three interfaces. No need to stitch together multiple libraries or write protocol parsers by hand.

**Real-Time Visibility** -- Track every channel, queue, agent, and conference room as events happen. The Live API maintains an in-memory model of your PBX state, updated in real time from AMI events. Know exactly what is happening on your Asterisk infrastructure at any moment, across multiple servers if needed.

**Call Session Intelligence** -- Raw AMI events are low-level and fragmented. The Session Engine automatically correlates them into unified call sessions with a state machine lifecycle -- ringing, answered, on hold, transferred, hung up. You work with calls, not protocol messages.

**Voice AI Ready** -- Add artificial intelligence to your calls with a pluggable Voice AI stack. Speech-to-text transcription, text-to-speech synthesis, custom conversation handlers, and direct OpenAI Realtime API integration are all included. Providers like Deepgram, ElevenLabs, Azure, Google, and Whisper can be swapped without changing application code.

**Production Hardened** -- The SDK includes 1,430 unit tests and 640 functional tests, produces zero compiler warnings, and passes AOT trim analysis cleanly. It supports multi-server federation with automatic agent routing, connection auto-reconnect with state reload, health check endpoints, and observability through System.Diagnostics.Metrics. It has been designed and tested for high-load scenarios exceeding 100,000 concurrent agents.

**Free Forever** -- Asterisk.Sdk is released under the MIT license. There are no per-minute fees, no seat licenses, no usage caps, and no feature gates. Use it in production, modify it, fork it, embed it in your product, distribute it to your customers. The full SDK is free for any purpose, commercial or otherwise.


## Who Is This For

**Companies with existing Asterisk deployments** that want to build custom .NET applications on top of their PBX infrastructure. If you already run Asterisk and your development team works in C#, this SDK bridges the gap without requiring a platform migration.

**ISVs and SaaS providers** building telephony products who need full control over their voice stack without cloud vendor lock-in. Ship your product with Asterisk as the engine and Asterisk.Sdk as the control plane, on your customers' infrastructure or your own.

**Contact center developers** who need a programmable foundation for custom call flows, interactive voice response systems, queue management, and agent desktop tools. The Live API and Session Engine handle the complexity of real-time call state so your team can focus on business logic.

**Teams evaluating Asterisk** who need confidence that a modern, well-maintained SDK exists for their .NET stack. The days of choosing between abandoned NuGet packages and raw socket programming are over.


## Technical Foundation

**.NET 10 with Native AOT** -- The SDK targets .NET 10 and is fully compatible with Native AOT publishing. Applications start in under 10 milliseconds, use minimal memory, and require no runtime reflection or just-in-time compilation. Every type is statically analyzed and verified at build time.

**Zero-copy TCP parsing** -- AMI and AGI protocol communication uses System.IO.Pipelines, the same high-performance I/O foundation that powers Kestrel. Incoming data is parsed directly from rental memory buffers without intermediate string allocations, reducing garbage collection pressure under high event throughput.

**Source generators replace runtime code generation** -- Four Roslyn source generators produce serialization and deserialization code at compile time for AMI actions, events, responses, and the event registry. This eliminates the reflection that typically prevents .NET libraries from working with AOT, while also improving runtime performance.

**19 composable NuGet packages** -- The SDK is modular by design. Install only the packages your application needs. The core alone is under 200 KB. Voice AI packages are completely separate from the AMI/AGI/ARI stack. Every package declares its dependencies explicitly, so your deployment includes only what you use.

**2,821 automated tests and 14 example applications** -- Every protocol parser, every event mapping, every state machine transition is covered by 2,597 unit tests plus 37 Sessions functional tests plus 154 Asterisk-against-real-PBX functional tests and 33 integration tests that run against a real Asterisk 23 instance in CI. The repository includes 14 runnable example applications demonstrating common integration patterns. A full PBX administration panel built with Blazor Server is available as a separate project.


## Enterprise Extension

For teams building enterprise contact centers, Asterisk.Sdk.Pro extends the MIT SDK with commercial features: skill-based routing with proficiency scoring, predictive and progressive dialer campaigns, real-time analytics dashboards, event sourcing for audit trails, multi-tenant isolation, and AI-powered agent assist with live coaching and automatic summarization.

Asterisk.Sdk.Pro is a separate commercial product that builds on top of the free SDK. The MIT SDK is complete on its own -- Pro adds features specific to large-scale contact center operations. For more information, see the Asterisk.Sdk.Pro repository.


## Get Started

For technical documentation, architecture details, and integration guides, see the developer guide at README-technical.md in this directory.

For the project overview, package list, and quick-start instructions, see the main README at the repository root.

The SDK is available on NuGet at https://www.nuget.org/packages/Asterisk.Sdk and can be installed with standard .NET tooling.


## License

Asterisk.Sdk is licensed under the MIT License. It is free for commercial and non-commercial use, modification, and redistribution. See the LICENSE file in the repository root for the full license text.
