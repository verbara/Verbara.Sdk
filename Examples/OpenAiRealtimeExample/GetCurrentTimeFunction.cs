using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;

namespace OpenAiRealtimeExample;

/// <summary>
/// Example function tool: returns the current UTC time as JSON.
/// A real function would query a database, call an API, look up caller info, etc.
/// </summary>
public sealed class GetCurrentTimeFunction : IRealtimeFunctionHandler
{
    public string Name => "get_current_time";
    public string Description => "Returns the current UTC date and time.";
    public string ParametersSchema => """{"type":"object","properties":{},"required":[]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult($"{{\"utc\":\"{now:O}\",\"readable\":\"{now:yyyy-MM-dd HH:mm:ss} UTC\"}}");
    }
}
