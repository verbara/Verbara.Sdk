using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Responses;

[AsteriskMapping("SkypeBuddy")]
public sealed class SkypeBuddyResponse : ManagerResponse
{
    public string? Skypename { get; set; }
    public string? Timezone { get; set; }
    public string? Availability { get; set; }
    public string? Fullname { get; set; }
    public string? Language { get; set; }
    public string? Country { get; set; }
    public string? PhoneHome { get; set; }
    public string? PhoneOffice { get; set; }
    public string? PhoneMobile { get; set; }
    public string? About { get; set; }
}

