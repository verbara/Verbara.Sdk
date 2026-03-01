using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeLicenseListComplete")]
public sealed class SkypeLicenseListCompleteEvent : ResponseEvent
{
}

