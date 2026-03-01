using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Registry")]
public sealed class RegistryEvent : ManagerEvent
{
    public string? ChannelType { get; set; }
    public string? ChannelDriver { get; set; }
    public string? Channel { get; set; }
    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? Status { get; set; }
    public string? Cause { get; set; }
    public string? Trunkname { get; set; }
}

