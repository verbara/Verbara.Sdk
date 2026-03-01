using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("JabberSend")]
public sealed class JabberSendAction : ManagerAction
{
    public string? Jabber { get; set; }
    public string? ScreenName { get; set; }
    public string? Message { get; set; }
}

