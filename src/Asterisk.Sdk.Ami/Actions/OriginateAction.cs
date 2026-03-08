using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Originate")]
public sealed class OriginateAction : ManagerAction, IEventGeneratingAction, IHasExtraFields
{
    public string? Account { get; set; }
    public string? CallerId { get; set; }
    public int? CallingPres { get; set; }
    public string? Channel { get; set; }
    public string? Context { get; set; }
    public string? Exten { get; set; }
    public int? Priority { get; set; }
    public string? Application { get; set; }
    public string? Data { get; set; }
    public long? Timeout { get; set; }
    [AsteriskMapping("Async")]
    public bool? IsAsync { get; set; }
    public bool? EarlyMedia { get; set; }
    public string? Codecs { get; set; }
    public string? ChannelId { get; set; }
    public string? OtherChannelId { get; set; }

    private List<KeyValuePair<string, string>>? _variables;

    /// <summary>
    /// Sets a channel variable on the originated call.
    /// Sent as "Variable: key=value" AMI headers.
    /// </summary>
    public void SetVariable(string key, string value)
    {
        _variables ??= [];
        _variables.Add(new("Variable", $"{key}={value}"));
    }

    IEnumerable<KeyValuePair<string, string>> IHasExtraFields.GetExtraFields() =>
        _variables ?? Enumerable.Empty<KeyValuePair<string, string>>();
}

