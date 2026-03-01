using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY DATETIME unixtime escapeDigits [format [timezone]]</summary>
public sealed class SayDateTimeCommand : AgiCommandBase
{
    public long? Time { get; set; }
    public string? EscapeDigits { get; set; }
    public string? Format { get; set; }
    public string? Timezone { get; set; }

    public override string BuildCommand()
    {
        var cmd = string.Create(CultureInfo.InvariantCulture, $"SAY DATETIME {Time ?? 0} {EscapeDigits}");

        if (Format is not null)
        {
            cmd += $" {Format}";
            if (Timezone is not null)
            {
                cmd += $" {Timezone}";
            }
        }

        return cmd;
    }
}
