using Asterisk.Sdk;

namespace Asterisk.Sdk.Agi.Mapping;

/// <summary>
/// Maps AGI script names to IAgiScript types by convention.
/// The script name in the AGI request is matched to registered types.
/// </summary>
public sealed class TypeNameMappingStrategy : IMappingStrategy
{
    private readonly Dictionary<string, Func<IAgiScript>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a script type factory for the given name.</summary>
    public void Register(string name, Func<IAgiScript> factory) =>
        _factories[name] = factory;

    /// <summary>Register a script type by its type name.</summary>
    public void Register<TScript>(Func<TScript> factory) where TScript : IAgiScript =>
        _factories[typeof(TScript).Name] = () => factory();

    public IAgiScript? Resolve(IAgiRequest request)
    {
        var script = request.Script;
        if (script is null) return null;

        // Extract script name from path
        var lastSlash = script.LastIndexOf('/');
        var name = lastSlash >= 0 ? script[(lastSlash + 1)..] : script;

        // Strip query parameters
        var queryIdx = name.IndexOf('?');
        if (queryIdx >= 0) name = name[..queryIdx];

        return _factories.TryGetValue(name, out var factory) ? factory() : null;
    }
}
