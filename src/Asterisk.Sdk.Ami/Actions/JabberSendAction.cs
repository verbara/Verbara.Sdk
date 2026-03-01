using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("JabberSend")]
public sealed class JabberSendAction : ManagerAction
{
    public string? Jabber { get; set; }
    public string? ScreenName { get; set; }
    public string? Message { get; set; }
}

