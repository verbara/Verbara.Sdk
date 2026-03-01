using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AsyncAgi")]
public sealed class AsyncAgiEvent : ResponseEvent
{
    public string? Channel { get; set; }
    public string? SubEvent { get; set; }
    public string? CommandId { get; set; }
    public string? Result { get; set; }
    public string? Env { get; set; }
}

