using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeLicenseListComplete")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeLicenseListCompleteEvent : ResponseEvent
{
}

