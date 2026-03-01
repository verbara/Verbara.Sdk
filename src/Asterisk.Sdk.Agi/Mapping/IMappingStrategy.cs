using Asterisk.Sdk;

namespace Asterisk.Sdk.Agi.Mapping;

/// <summary>
/// Strategy for mapping incoming AGI requests to script implementations.
/// </summary>
public interface IMappingStrategy
{
    /// <summary>Determine which AGI script should handle the given request.</summary>
    IAgiScript? Resolve(IAgiRequest request);
}
