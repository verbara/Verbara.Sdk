// Asterisk.Sdk - Contact-Center Supervision Example
// Demonstrates the four supervisor/transfer activities added in v1.11.0:
//   - AttendedTransferActivity (AMI Atxfer, via new AmiActivityBase)
//   - ChanSpyActivity (silent listen, whisper-only, coach)
//   - BargeActivity (audible three-way intrusion)
//   - SnoopActivity (ARI snoop channel)
//
// In production these run against a live IAgiChannel / IAriClient / IAmiConnection.
// This demo substitutes each dependency with NSubstitute so it runs without a PBX
// and prints exactly what would be dispatched to Asterisk in each case.

using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Activities;
using Asterisk.Sdk.Activities.Models;
using Asterisk.Sdk.Ami.Actions;
using NSubstitute;

Console.WriteLine("Contact-Center Supervision — activity dispatch demo");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

// 1. ATTENDED TRANSFER — AMI Atxfer action on the bridged peer.
{
    var ami = Substitute.For<IAmiConnection>();
    var activity = new AttendedTransferActivity(ami)
    {
        Channel = "PJSIP/alice-00000001",
        Destination = new DialPlanExtension("internal", "2001"),
    };

    await activity.StartAsync();
    var actions = ami.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "SendActionAsync");
    Console.WriteLine($"[AttendedTransfer] Status={activity.Status}");
    Console.WriteLine($"  → AMI SendAction dispatched {actions.Count()} time(s).");
    Console.WriteLine($"    channel={activity.Channel}  context={activity.Destination.Context}  exten={activity.Destination.Extension}");
    Console.WriteLine();
}

// 2. CHANSPY — silent listen, whisper-only, and coach modes.
{
    foreach (var mode in new[] { ChanSpyMode.Both, ChanSpyMode.SpyOnly, ChanSpyMode.WhisperOnly, ChanSpyMode.Coach })
    {
        var channel = Substitute.For<IAgiChannel>();
        var activity = new ChanSpyActivity(channel)
        {
            TargetChannel = "PJSIP/alice",
            Mode = mode,
            Options = "qE",
        };

        await activity.StartAsync();
        var call = channel.ReceivedCalls().First(c => c.GetMethodInfo().Name == "ExecAsync");
        var execArgs = call.GetArguments();
        Console.WriteLine($"[ChanSpy mode={mode,-12}] AGI Exec('{execArgs[0]}', '{execArgs[1]}')");
    }
    Console.WriteLine();
}

// 3. BARGE — ChanSpy with the 'B' flag so the supervisor becomes audible.
{
    var channel = Substitute.For<IAgiChannel>();
    var activity = new BargeActivity(channel)
    {
        TargetChannel = "PJSIP/alice",
        Options = "q",
    };

    await activity.StartAsync();
    var call = channel.ReceivedCalls().First(c => c.GetMethodInfo().Name == "ExecAsync");
    var execArgs = call.GetArguments();
    Console.WriteLine($"[Barge] AGI Exec('{execArgs[0]}', '{execArgs[1]}')  -- 'B' prefix adds barge-in");
    Console.WriteLine();
}

// 4. SNOOP — ARI snoop channel (supervisor listens + optionally whispers via Stasis app).
{
    var ari = Substitute.For<IAriClient>();
    var channels = Substitute.For<IAriChannelsResource>();
    ari.Channels.Returns(channels);

    var snoopChannel = new AriChannel { Id = "snoop-001", Name = "Snoop/snoop-001" };
#pragma warning disable CA2012
    channels.SnoopAsync(
        Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
        Arg.Any<CancellationToken>())
        .Returns(new ValueTask<AriChannel>(snoopChannel));
#pragma warning restore CA2012

    var activity = new SnoopActivity(ari)
    {
        TargetChannelId = "PJSIP/alice-00000001",
        App = "supervisor-coach",
        Spy = SnoopDirection.Both,
        Whisper = SnoopDirection.In,
    };

    await activity.StartAsync();
    Console.WriteLine($"[Snoop] ARI SnoopAsync dispatched. SnoopChannel={activity.SnoopChannel?.Id}");
    Console.WriteLine($"  spy={activity.Spy}  whisper={activity.Whisper}  app={activity.App}");
    Console.WriteLine();
}

Console.WriteLine("All four supervision activities completed their demo dispatches.");
Console.WriteLine("In production these dispatch real actions against a live Asterisk node.");
