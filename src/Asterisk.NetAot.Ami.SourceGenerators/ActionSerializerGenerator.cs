using Microsoft.CodeAnalysis;

namespace Asterisk.NetAot.Ami.SourceGenerators;

/// <summary>
/// Source generator that creates AOT-compatible serializers for ManagerAction subclasses.
/// Scans for classes with [AsteriskMapping] and generates Write methods without reflection.
/// </summary>
[Generator]
public sealed class ActionSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // TODO: Register syntax provider to find ManagerAction subclasses
        // TODO: Generate serialization code per action class
    }
}
