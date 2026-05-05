using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeLicenseListComplete")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeLicenseListCompleteEvent : ResponseEvent
{
}

