using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC Dial — dials a destination through the Dial application.</summary>
public sealed class DialCommand : AgiCommandBase
{
    public string? Target { get; set; }
    public int? Timeout { get; set; }
    public string? Options { get; set; }

    public override string BuildCommand()
    {
        var cmd = $"EXEC Dial {Target}";

        if (Timeout.HasValue || Options is not null)
        {
            cmd += string.Create(CultureInfo.InvariantCulture, $",{Timeout ?? 0}");

            if (Options is not null)
            {
                cmd += $",{Options}";
            }
        }

        return cmd;
    }
}
