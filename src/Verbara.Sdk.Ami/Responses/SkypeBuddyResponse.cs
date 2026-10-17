using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Responses;

[Obsolete(
    "Skype for Asterisk was discontinued by its publisher in 2011 (not sold after 2011-07-26); no Asterisk answers SkypeBuddy. No replacement. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("SkypeBuddy")]
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

