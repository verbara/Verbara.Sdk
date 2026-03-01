namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC ConfBridge — joins the current channel into a ConfBridge conference.</summary>
public sealed class ConfbridgeCommand : AgiCommandBase
{
    public string? Conference { get; set; }
    public string? BridgeProfile { get; set; }
    public string? UserProfile { get; set; }
    public string? Menu { get; set; }

    public override string BuildCommand()
    {
        var cmd = $"EXEC ConfBridge {Conference}";

        if (BridgeProfile is not null || UserProfile is not null || Menu is not null)
        {
            cmd += $",{BridgeProfile}";

            if (UserProfile is not null || Menu is not null)
            {
                cmd += $",{UserProfile}";

                if (Menu is not null)
                {
                    cmd += $",{Menu}";
                }
            }
        }

        return cmd;
    }
}
