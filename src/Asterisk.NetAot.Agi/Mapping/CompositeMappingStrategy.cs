using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Agi.Mapping;

/// <summary>
/// Combines multiple mapping strategies. Tries each one in order until a match is found.
/// </summary>
public sealed class CompositeMappingStrategy : IMappingStrategy
{
    private readonly List<IMappingStrategy> _strategies = [];

    public CompositeMappingStrategy(params IMappingStrategy[] strategies)
    {
        _strategies.AddRange(strategies);
    }

    /// <summary>Add a strategy to the chain.</summary>
    public void Add(IMappingStrategy strategy) => _strategies.Add(strategy);

    public IAgiScript? Resolve(IAgiRequest request)
    {
        foreach (var strategy in _strategies)
        {
            var script = strategy.Resolve(request);
            if (script is not null)
                return script;
        }

        return null;
    }
}
