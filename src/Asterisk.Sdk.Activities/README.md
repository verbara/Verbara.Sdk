# Asterisk.Sdk.Activities

High-level call activity abstractions for Asterisk AGI — async state-machine-driven operations for common telephony workflows.

## Installation

```bash
dotnet add package Asterisk.Sdk.Activities
```

## Quick Start

```csharp
// Inside a FastAGI script handler
["dial-out.agi"] = async channel =>
{
    await channel.AnswerAsync();

    var dial = new DialActivity(channel)
    {
        Target = new EndPoint("SIP/1001"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    await dial.StartAsync();
    Console.WriteLine($"Dial result: {dial.DialStatus}"); // ANSWER, BUSY, NOANSWER, etc.

    if (dial.Status == ActivityStatus.Completed)
    {
        var hold = new HoldActivity(channel) { MusicOnHoldClass = "default" };
        await hold.StartAsync();
    }

    await channel.HangupAsync();
}
```

## Features

- `DialActivity` — originate outbound call; exposes `DialStatus` (ANSWER, BUSY, NOANSWER, CANCEL, CONGESTION)
- `HoldActivity` — put a channel on music-on-hold with configurable class
- `BridgeActivity` — bridge the current channel to a target channel by name
- `HangupActivity`, `PlayMessageActivity`, `QueueActivity`, `ParkActivity`, `BlindTransferActivity`, `MeetmeActivity`, `VoicemailActivity`, `ExternalMediaActivity`
- `IActivity` — common interface: `StartAsync()`, `CancelAsync()`, `Status`, `StatusChanges` (`IObservable<ActivityStatus>`)
- Native AOT compatible

## Documentation

See the [main README](../../README.md) for full documentation.
