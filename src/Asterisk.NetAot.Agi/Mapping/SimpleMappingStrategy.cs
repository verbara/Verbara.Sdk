using System.Collections.Concurrent;
using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Agi.Mapping;

/// <summary>
/// Maps AGI script names to IAgiScript instances using a dictionary.
/// </summary>
public sealed class SimpleMappingStrategy : IMappingStrategy
{
    private readonly ConcurrentDictionary<string, IAgiScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a script for the given name.</summary>
    public void Add(string scriptName, IAgiScript script) =>
        _scripts[scriptName] = script;

    /// <summary>Remove a script mapping.</summary>
    public bool Remove(string scriptName) =>
        _scripts.TryRemove(scriptName, out _);

    public IAgiScript? Resolve(IAgiRequest request)
    {
        var script = request.Script;
        if (script is null) return null;

        // Try exact match first
        if (_scripts.TryGetValue(script, out var found))
            return found;

        // Try stripping path prefix (e.g., "agi://host/ScriptName" -> "ScriptName")
        var lastSlash = script.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var name = script[(lastSlash + 1)..];
            if (_scripts.TryGetValue(name, out found))
                return found;
        }

        return null;
    }
}
