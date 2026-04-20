namespace Asterisk.Sdk.Resilience;

/// <summary>
/// Thrown by <see cref="ResiliencePolicy"/> when an action is attempted while the
/// per-key circuit breaker is in the open state.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    /// <summary>Creates a new exception for the given key.</summary>
    public CircuitBreakerOpenException(string key)
        : base($"Circuit breaker is open for key '{key}'.")
    {
        Key = key;
    }

    /// <summary>Creates a new exception with a custom message for the given key.</summary>
    public CircuitBreakerOpenException(string key, string message)
        : base(message)
    {
        Key = key;
    }

    /// <summary>The logical key whose circuit is open.</summary>
    public string Key { get; }
}
