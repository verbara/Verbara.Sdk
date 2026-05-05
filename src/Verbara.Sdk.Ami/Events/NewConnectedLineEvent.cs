using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("NewConnectedLine")]
public sealed class NewConnectedLineEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? AccountCode { get; set; }
    public string? LinkedId { get; set; }
}

