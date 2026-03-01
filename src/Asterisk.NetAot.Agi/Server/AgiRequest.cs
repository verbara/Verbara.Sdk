using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Agi.Server;

/// <summary>
/// Represents an incoming AGI request from Asterisk.
/// Parsed from the initial key: value block sent when AGI connection is established.
/// </summary>
public sealed class AgiRequest : IAgiRequest
{
    private readonly Dictionary<string, string> _variables;

    public string? Script { get; }
    public string? Channel { get; }
    public string? UniqueId { get; }
    public string? CallerId { get; }
    public string? CallerIdName { get; }
    public string? Context { get; }
    public string? Extension { get; }
    public int Priority { get; }
    public string? Language { get; }
    public bool IsNetwork { get; }

    /// <summary>All raw AGI variables.</summary>
    public IReadOnlyDictionary<string, string> Variables => _variables;

    private AgiRequest(Dictionary<string, string> variables)
    {
        _variables = variables;
        Script = Get("agi_network_script") ?? Get("agi_request");
        Channel = Get("agi_channel");
        UniqueId = Get("agi_uniqueid");
        CallerId = Get("agi_callerid");
        CallerIdName = Get("agi_calleridname");
        Context = Get("agi_context");
        Extension = Get("agi_extension");
        Priority = int.TryParse(Get("agi_priority"), out var p) ? p : 1;
        Language = Get("agi_language");
        IsNetwork = string.Equals(Get("agi_network"), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private string? Get(string key) =>
        _variables.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Parse AGI request from key: value lines.
    /// Lines are in format "agi_key: value" terminated by a blank line.
    /// </summary>
    public static AgiRequest Parse(IEnumerable<string> lines)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();
                vars[key] = value;
            }
        }

        return new AgiRequest(vars);
    }
}
