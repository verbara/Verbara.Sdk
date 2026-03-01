using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: CONTROL STREAM FILE filename escapeDigits [skipms [ff [rew [pause]]]]</summary>
public sealed class ControlStreamFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Offset { get; set; }
    public string? ForwardDigit { get; set; }
    public string? RewindDigit { get; set; }
    public string? PauseDigit { get; set; }

    public override string BuildCommand()
    {
        var cmd = $"CONTROL STREAM FILE {File} {EscapeDigits}";

        if (Offset.HasValue)
        {
            cmd += string.Create(CultureInfo.InvariantCulture, $" {Offset.Value}");

            if (ForwardDigit is not null)
            {
                cmd += $" {ForwardDigit}";

                if (RewindDigit is not null)
                {
                    cmd += $" {RewindDigit}";

                    if (PauseDigit is not null)
                    {
                        cmd += $" {PauseDigit}";
                    }
                }
            }
        }

        return cmd;
    }
}
