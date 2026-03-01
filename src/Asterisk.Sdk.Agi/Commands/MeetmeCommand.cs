namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC MeetMe — joins the current channel into a MeetMe conference.</summary>
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
