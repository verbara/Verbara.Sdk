# Asterisk.Sdk.Activities

High-level call activity abstractions built on top of AMI, AGI, and Live layers. Provides state-machine-driven activities for common telephony operations.

## Features

- `DialActivity` — originate and track outbound calls
- `HoldActivity` — manage hold/unhold transitions
- `BridgeActivity` — bridge channels with state tracking
- Reactive state machines via `System.Reactive`
- Native AOT compatible
