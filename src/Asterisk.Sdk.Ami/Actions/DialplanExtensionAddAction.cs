using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("DialplanExtensionAdd")]
public sealed class DialplanExtensionAddAction : ManagerAction
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public int? Priority { get; set; }
    public string? Application { get; set; }
    public string? ApplicationData { get; set; }
    public bool? Replace { get; set; }
}
