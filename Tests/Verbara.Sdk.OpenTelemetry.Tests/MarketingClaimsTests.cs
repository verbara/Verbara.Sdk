using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Sdk;
using Verbara.Sdk.Hosting;

namespace Verbara.Sdk.OpenTelemetry.Tests;

/// <summary>
/// Pins the marketing counts cited in <c>README.md</c> "Observability" section
/// against the SDK source of truth. Each test fails if the SDK adds (or removes)
/// a telemetry signal without the README being updated in the same PR — the
/// explicit reverse-coupling that keeps "claim → implementation" honest.
///
/// When intentionally adding a new ActivitySource / Meter / HealthCheck /
/// SemanticConvention:
///   1. Update the expected number below.
///   2. Update <c>README.md</c> "Observability" section to match.
///   3. Update <c>CHANGELOG.md</c>.
/// </summary>
[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Reflection is OK in test code")]
[SuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers", Justification = "Reflection is OK in test code")]
public sealed class MarketingClaimsTests
{
    // Force-load every SDK assembly that ships a HealthCheck so the reflection
    // pass below sees their types. Touching a type triggers the JIT/AOT loader.
    private static readonly Type[] _assemblyAnchors =
    [
        typeof(Verbara.Sdk.IVerbaraServer),
        typeof(Verbara.Sdk.Hosting.VerbaraTelemetry),
        typeof(Verbara.Sdk.Ami.Diagnostics.AmiHealthCheck),
        typeof(Verbara.Sdk.Agi.Diagnostics.AgiHealthCheck),
        typeof(Verbara.Sdk.Ari.Diagnostics.AriHealthCheck),
        typeof(Verbara.Sdk.Live.Diagnostics.LiveHealthCheck),
        typeof(Verbara.Sdk.Sessions.Diagnostics.SessionHealthCheck),
        typeof(Verbara.Sdk.Push.Diagnostics.PushHealthCheck),
        typeof(Verbara.Sdk.VoiceAi.Diagnostics.VoiceAiHealthCheck),
        typeof(Verbara.Sdk.VoiceAi.Stt.Diagnostics.SttHealthCheck),
        typeof(Verbara.Sdk.VoiceAi.Tts.Diagnostics.TtsHealthCheck),
        typeof(Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics.AudioSocketHealthCheck),
        typeof(Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics.RealtimeHealthCheck),
    ];

    [Fact]
    public void ActivitySourceNames_Count_ShouldMatchReadmeClaim()
    {
        // README.md "Observability" claims 9 ActivitySources.
        VerbaraTelemetry.ActivitySourceNames.Should().HaveCount(9);
    }

    [Fact]
    public void MeterNames_Count_ShouldMatchReadmeClaim()
    {
        // README.md "Observability" claims 15 Meters.
        VerbaraTelemetry.MeterNames.Should().HaveCount(15);
    }

    [Fact]
    public void VerbaraSemanticConventions_ConstStringCount_ShouldMatchReadmeClaim()
    {
        // README.md "Observability" claims 60 const strings across 14 nested classes.
        var nestedClasses = typeof(VerbaraSemanticConventions)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .Where(t => t.IsAbstract && t.IsSealed) // i.e. C# `static class`
            .ToList();

        var constStringCount = nestedClasses
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Count(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));

        nestedClasses.Should().HaveCount(14);
        constStringCount.Should().Be(60);
    }

    [Fact]
    public void IHealthCheck_ImplementationCount_ShouldMatchReadmeClaim()
    {
        // README.md "Observability" claims 11 IHealthCheck implementations.
        // Sweep every loaded Verbara.Sdk.* assembly (the `_assemblyAnchors` array
        // above forces them to load) and count concrete IHealthCheck types.
        var anchorAssemblies = _assemblyAnchors
            .Select(t => t.Assembly)
            .Distinct()
            .ToList();

        var healthCheckTypes = anchorAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IHealthCheck).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        healthCheckTypes.Should().HaveCount(11);
    }
}
