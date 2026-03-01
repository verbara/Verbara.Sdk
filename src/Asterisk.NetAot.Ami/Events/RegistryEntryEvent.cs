using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("RegistryEntry")]
public sealed class RegistryEntryEvent : ResponseEvent
{
    public long? RegistrationTime { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Host { get; set; }
    public string? State { get; set; }
    public int? Refresh { get; set; }
}

