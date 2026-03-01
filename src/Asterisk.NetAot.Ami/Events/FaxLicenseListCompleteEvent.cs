using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("FaxLicenseListComplete")]
public sealed class FaxLicenseListCompleteEvent : ResponseEvent
{
}

