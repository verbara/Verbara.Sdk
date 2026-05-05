using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("NewAccountCode")]
public sealed class NewAccountCodeEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? AccountCode { get; set; }
    public string? OldAccountCode { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
}

