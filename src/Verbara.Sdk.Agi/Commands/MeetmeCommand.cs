namespace Verbara.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC MeetMe — joins the current channel into a MeetMe conference.</summary>
[Obsolete(
    "app_meetme is deprecated since Asterisk 19 and is not built by default. Use ConfbridgeCommand. This type is removed in Verbara.Sdk 2.10.0.",
    DiagnosticId = "VSDK0005",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
public sealed class MeetmeCommand : AgiCommandBase
{
    public string? Conference { get; set; }
    public string? Options { get; set; }
    public string? Pin { get; set; }

    public override string BuildCommand()
    {
        var cmd = $"EXEC MeetMe {Conference}";

        if (Options is not null || Pin is not null)
        {
            cmd += $",{Options}";

            if (Pin is not null)
            {
                cmd += $",{Pin}";
            }
        }

        return cmd;
    }
}
