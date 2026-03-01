using Microsoft.CodeAnalysis;

namespace Asterisk.NetAot.Ami.SourceGenerators;

/// <summary>
/// Source generator that creates a FrozenDictionary mapping event type names
/// to factory delegates. Replaces classpath scanning with compile-time discovery.
/// </summary>
[Generator]
public sealed class EventRegistryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // TODO: Collect all ManagerEvent subclasses
        // TODO: Generate static registry: FrozenDictionary<string, Func<ManagerEvent>>
    }
}
