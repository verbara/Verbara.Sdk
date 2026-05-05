using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AsyncAgi")]
public sealed class AsyncAgiEvent : ResponseEvent
{
    public string? Channel { get; set; }
    public string? SubEvent { get; set; }
    public string? CommandId { get; set; }
    public string? Result { get; set; }
    public string? Env { get; set; }
}

