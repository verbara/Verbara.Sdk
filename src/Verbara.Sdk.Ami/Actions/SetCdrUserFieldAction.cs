using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[Obsolete(
    "app_setcdruserfield was removed in Asterisk 1.6 (deprecated in 1.4); no supported Asterisk accepts SetCDRUserField. Use SetVarAction with CDR(userfield). This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
[VerbaraMapping("SetCDRUserField")]
public sealed class SetCdrUserFieldAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? UserField { get; set; }
    public bool? Append { get; set; }
}

