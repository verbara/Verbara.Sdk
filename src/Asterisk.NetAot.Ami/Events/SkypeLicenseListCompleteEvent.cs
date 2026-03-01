using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeLicenseListComplete")]
public sealed class SkypeLicenseListCompleteEvent : ResponseEvent
{
}

