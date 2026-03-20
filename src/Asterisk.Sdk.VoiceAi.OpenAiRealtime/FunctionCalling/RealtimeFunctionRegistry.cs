using System.Diagnostics.CodeAnalysis;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

/// <summary>
/// Collects all registered <see cref="IRealtimeFunctionHandler"/> implementations
/// and provides fast lookup by function name.
/// Registered as a singleton by <c>AddOpenAiRealtimeBridge()</c>.
/// </summary>
internal sealed class RealtimeFunctionRegistry
{
    private readonly Dictionary<string, IRealtimeFunctionHandler> _handlers;

    /// <summary>Initializes the registry from a collection of handlers.</summary>
    public RealtimeFunctionRegistry(IEnumerable<IRealtimeFunctionHandler> handlers)
    {
        _handlers = new Dictionary<string, IRealtimeFunctionHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (!_handlers.TryAdd(h.Name, h))
                throw new InvalidOperationException(
                    $"Duplicate function handler registered for name '{h.Name}'. Each handler must have a unique Name.");
        }
    }

    /// <summary>All registered handlers — used by <c>BuildSessionUpdate</c> to enumerate tools.</summary>
    public IReadOnlyCollection<IRealtimeFunctionHandler> AllHandlers => _handlers.Values;

    /// <summary>
    /// Looks up a handler by function name.
    /// Returns <c>false</c> if no handler is registered for <paramref name="name"/>.
    /// </summary>
    public bool TryGetHandler(string name, [NotNullWhen(true)] out IRealtimeFunctionHandler? handler)
        => _handlers.TryGetValue(name, out handler);
}
