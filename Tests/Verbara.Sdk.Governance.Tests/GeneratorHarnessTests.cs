using Verbara.Sdk.Ami.SourceGenerators;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The harness itself: over a compilation that declares one typed event, the registry generator runs, maps the event's
/// wire name to its type, and the compilation it leaves builds clean. A harness that compiled nothing, or that lost
/// the generated file, would make every test written on it read the absence of output as a pass.
/// </summary>
public sealed class GeneratorHarnessTests
{
    private const string OneTypedEvent = """
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartEvent : ManagerEvent
        {
            public string? Channel { get; set; }
        }
        """;

    [Fact]
    public void Run_ShouldGenerateTheRegistryEntry_WhenTheCompilationDeclaresOneTypedEvent()
    {
        var run = GeneratorHarness.Run(OneTypedEvent, new EventRegistryGenerator());

        var registry = run.Single(nameof(EventRegistryGenerator));
        registry.HintName.Should().Be("GeneratedEventRegistry.g.cs");
        registry.Text.Should().Contain("[\"ProbeStart\"] = static () => new global::Probe.ProbeStartEvent()",
            "the registry maps the event's wire name to a factory of its type");
        run.GeneratorDiagnostics.Should().BeEmpty("the generator reports nothing on a well-formed event");
        run.WarningsAndErrors.Should().BeEmpty("the stubs and the generated registry compile together without a diagnostic");
    }

    [Fact]
    public void Run_ShouldReportTheCompilationsErrors_WhenTheSourceDoesNotCompile()
    {
        // The control: the harness reads the compilation's diagnostics, so an empty list above is a measurement.
        var run = GeneratorHarness.Run("namespace Probe; public sealed class Broken : MissingBase { }",
            new EventRegistryGenerator());

        run.WarningsAndErrors.Should().ContainSingle(d => d.Contains("CS0246", StringComparison.Ordinal),
            "a base type that does not exist is a compile error the harness must surface");
    }

    [Fact]
    public void Run_ShouldGenerateBothFiles_WhenBothEventGeneratorsRun()
    {
        var run = GeneratorHarness.Run(OneTypedEvent, new EventRegistryGenerator(), new EventDeserializerGenerator());

        run.Single(nameof(EventRegistryGenerator)).HintName.Should().Be("GeneratedEventRegistry.g.cs");
        run.Single(nameof(EventDeserializerGenerator)).Text.Should().Contain(
            "case global::Probe.ProbeStartEvent e:", "the deserializer fills the event's own properties");
        run.WarningsAndErrors.Should().BeEmpty("the generated deserializer binds only what the stubs declare");
    }
}
