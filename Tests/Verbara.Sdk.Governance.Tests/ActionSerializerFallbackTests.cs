using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Verbara.Sdk.Ami.SourceGenerators;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The generated action serializer has an arm for every mapped action type and refuses any other: it never writes an
/// action under a name guessed from its class name (every <c>Action</c> removed) with none of its fields. Each case
/// runs the generator over a synthetic compilation (<see cref="GeneratorHarness"/>), then emits and runs the result,
/// so the refusal is observed as a caller sees it, not read from the generated text alone.
/// </summary>
[SuppressMessage("Trimming", "IL2026", Justification = "Test code: loads and reads an assembly it just compiled, never trimmed here.")]
[SuppressMessage("Trimming", "IL2070", Justification = "Test code: reads a type of an assembly it just compiled, never trimmed here.")]
[SuppressMessage("Trimming", "IL2072", Justification = "Test code: instantiates a type of an assembly it just compiled, never trimmed here.")]
[SuppressMessage("Trimming", "IL2075", Justification = "Test code: reads a type of an assembly it just compiled, never trimmed here.")]
public sealed class ActionSerializerFallbackTests
{
    private const string Source = """
        using System.Collections.Generic;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Probe;

        [VerbaraMapping("ProbeStart")]
        public sealed class ProbeStartAction : ManagerAction
        {
            public string? Channel { get; set; }
        }

        public sealed class ConsumerProbeAction : ManagerAction, IHasExtraFields
        {
            public string? Queue { get; set; }

            public IEnumerable<KeyValuePair<string, string>> GetExtraFields() => [new("Channel", "PJSIP/2000")];
        }

        public sealed class LoggerRotateAction : ManagerAction
        {
        }
        """;

    [Fact]
    public void GeneratedSerializer_ShouldNotDeriveANameFromTheClassName_WhenGenerated()
    {
        var run = GeneratorHarness.Run(Source, new ActionSerializerGenerator());
        var text = run.Single(nameof(ActionSerializerGenerator)).Text;

        using (new FluentAssertions.Execution.AssertionScope())
        {
            text.Should().NotContain("GetType().Name", "no action is written under a name derived from its class name");
            text.Should().NotContain("_ => Array.Empty", "no unknown action is written with no fields");
            run.WarningsAndErrors.Should().BeEmpty("the fallback names no SDK type, so the output compiles over the stubs");
        }
    }

    [Theory]
    [InlineData("Probe.ConsumerProbeAction", "GetActionName")]
    [InlineData("Probe.ConsumerProbeAction", "Serialize")]
    [InlineData("Probe.LoggerRotateAction", "GetActionName")]
    [InlineData("Probe.LoggerRotateAction", "Serialize")]
    public void GeneratedSerializer_ShouldThrowArgumentExceptionNamingTypeAndRawAction_WhenActionTypeIsNotMapped(
        string typeName, string method)
    {
        var (serializer, assembly, context) = EmitAndLoad();
        try
        {
            var action = Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!)!;

            var act = () => Invoke(serializer, method, action);

            var thrown = act.Should().Throw<ArgumentException>().Which;
            thrown.ParamName.Should().Be("action");
            thrown.Message.Should().Contain(typeName).And.Contain("RawAction");
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void GeneratedSerializer_ShouldWriteTheMappedNameAndFields_WhenActionTypeIsMapped()
    {
        var (serializer, assembly, context) = EmitAndLoad();
        try
        {
            var type = assembly.GetType("Probe.ProbeStartAction", throwOnError: true)!;
            var action = Activator.CreateInstance(type)!;
            type.GetProperty("Channel")!.SetValue(action, "PJSIP/2000");

            Invoke(serializer, "GetActionName", action).Should().Be("ProbeStart");
            ((IEnumerable<KeyValuePair<string, string>>)Invoke(serializer, "Serialize", action)!)
                .Should().Equal(new[] { new KeyValuePair<string, string>("Channel", "PJSIP/2000") });
        }
        finally
        {
            context.Unload();
        }
    }

    private static object? Invoke(Type serializer, string method, object action)
    {
        try
        {
            return serializer.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [action]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }

    private static (Type Serializer, Assembly Assembly, AssemblyLoadContext Context) EmitAndLoad()
    {
        var run = GeneratorHarness.Run(Source, new ActionSerializerGenerator());
        run.WarningsAndErrors.Should().BeEmpty();

        using var image = new MemoryStream();
        var emitted = run.Output.Emit(image);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;

        var context = new AssemblyLoadContext(nameof(ActionSerializerFallbackTests), isCollectible: true);
        var assembly = context.LoadFromStream(image);
        var serializer = assembly.GetType("Verbara.Sdk.Ami.Generated.GeneratedActionSerializer", throwOnError: true)!;
        return (serializer, assembly, context);
    }
}
