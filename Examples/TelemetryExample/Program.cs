using System.Diagnostics;
using System.Diagnostics.Metrics;
using Verbara.Sdk.Hosting;
using Microsoft.Extensions.Hosting;

// This example demonstrates how to discover every ActivitySource and Meter
// registered by the Asterisk.Sdk stack at runtime and consume them without
// hard-coding any names. The lists grow automatically as new packages register
// telemetry — consumer code written today keeps working for future releases.
//
// For brevity this sample uses the lower-level System.Diagnostics listeners
// instead of the OpenTelemetry SDK. The README.md shows the OpenTelemetry
// equivalent (add the three OpenTelemetry.* NuGet packages, then call
// builder.Services.AddOpenTelemetry().WithTracing(...).WithMetrics(...)).

Console.WriteLine($"ActivitySources registered: {VerbaraTelemetry.ActivitySourceNames.Length}");
foreach (var name in VerbaraTelemetry.ActivitySourceNames)
    Console.WriteLine($"  activity: {name}");

Console.WriteLine();
Console.WriteLine($"Meters registered: {VerbaraTelemetry.MeterNames.Length}");
foreach (var name in VerbaraTelemetry.MeterNames)
    Console.WriteLine($"  meter:    {name}");

Console.WriteLine();
Console.WriteLine("Attaching listeners. Press Ctrl+C to stop.");

// --- ActivitySource listener: prints every span to the console ---
using var activityListener = new ActivityListener
{
    ShouldListenTo = src =>
        VerbaraTelemetry.ActivitySourceNames.Contains(src.Name),
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
    ActivityStarted = act =>
        Console.WriteLine($"[span-start] {act.Source.Name} :: {act.OperationName} ({act.Id})"),
    ActivityStopped = act =>
        Console.WriteLine($"[span-stop ] {act.Source.Name} :: {act.OperationName} ({act.Duration.TotalMilliseconds:0.0} ms)"),
};
ActivitySource.AddActivityListener(activityListener);

// --- Meter listener: prints every measurement to the console ---
using var meterListener = new MeterListener
{
    InstrumentPublished = (instrument, listener) =>
    {
        if (VerbaraTelemetry.MeterNames.Contains(instrument.Meter.Name))
            listener.EnableMeasurementEvents(instrument);
    },
};
meterListener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
    Console.WriteLine($"[metric   ] {inst.Meter.Name}/{inst.Name} += {value}"));
meterListener.SetMeasurementEventCallback<double>((inst, value, _, _) =>
    Console.WriteLine($"[metric   ] {inst.Meter.Name}/{inst.Name} = {value:F3}"));
meterListener.Start();

// Start an empty host so the process stays alive; in a real app you would
// call services.AddVerbara(...) / AddVoiceAiPipeline<T>() / AddSessionsCore()
// here and the listeners above would automatically pick up the traffic.
var host = Host.CreateDefaultBuilder(args).Build();
await host.RunAsync();
