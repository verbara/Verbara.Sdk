using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Filter")]
public sealed class FilterAction : ManagerAction
{
    public string? Operation { get; set; }
    public string? Filter { get; set; }
}

