using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("FaxLicenseListComplete")]
public sealed class FaxLicenseListCompleteEvent : ResponseEvent
{
}

