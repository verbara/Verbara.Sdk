using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "Skype for Asterisk was discontinued by its publisher in 2011 (not sold after 2011-07-26); no Asterisk accepts SkypeRemoveBuddy. No replacement. Removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("SkypeRemoveBuddy")]
public sealed class SkypeRemoveBuddyAction : ManagerAction
{
    public string? User { get; set; }
    public string? Buddy { get; set; }
}

