namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

/// <summary>
/// Represents a tool (function) that can be invoked by the OpenAI Realtime model.
/// Implementations must be registered as singleton or transient — never scoped.
/// </summary>
public interface IRealtimeFunctionHandler
{
    /// <summary>The unique function name sent to OpenAI in the session configuration.</summary>
    string Name { get; }

    /// <summary>Human-readable description of what the function does.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema literal that describes the function's parameters.
    /// Inserted verbatim into <c>session.update</c> via <c>Utf8JsonWriter.WriteRawValue</c>.
    /// </summary>
    string ParametersSchema { get; }

    /// <summary>
    /// Executes the function and returns a JSON string result.
    /// On failure, return a JSON error object — do not throw.
    /// </summary>
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
