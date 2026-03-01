using Microsoft.CodeAnalysis;

namespace Asterisk.NetAot.Ami.SourceGenerators;

/// <summary>
/// Source generator that creates AOT-compatible deserializers for ManagerEvent subclasses.
/// Replaces the reflection-based EventBuilderImpl from asterisk-java.
/// </summary>
[Generator]
public sealed class EventDeserializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // TODO: Register syntax provider to find ManagerEvent subclasses
        // TODO: Generate deserialization code per event class
    }
}
