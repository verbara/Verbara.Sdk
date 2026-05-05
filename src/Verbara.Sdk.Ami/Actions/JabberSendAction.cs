using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("JabberSend")]
public sealed class JabberSendAction : ManagerAction
{
    public string? Jabber { get; set; }
    public string? ScreenName { get; set; }
    public string? Message { get; set; }
}

