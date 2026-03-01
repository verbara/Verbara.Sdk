using Asterisk.Sdk;

namespace Asterisk.Sdk.Ami.Events.Base;

/// <summary>Base class for security events (auth failures, ACL violations).</summary>
public class SecurityEventBase : ManagerEvent
{
    public string? EventTV { get; set; }
    public string? Severity { get; set; }
    public string? Service { get; set; }
    public string? AccountID { get; set; }
    public string? SessionID { get; set; }
    public string? LocalAddress { get; set; }
    public string? RemoteAddress { get; set; }
    public string? SessionTV { get; set; }
}
