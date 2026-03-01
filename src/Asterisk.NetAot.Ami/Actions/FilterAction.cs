using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Filter")]
public sealed class FilterAction : ManagerAction
{
    public string? Operation { get; set; }
    public string? Filter { get; set; }
}

