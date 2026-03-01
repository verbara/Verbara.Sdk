using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: RECORD FILE filename format escapeDigits timeout [offset [beep [s=silence]]]</summary>
public sealed class RecordFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? Format { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Timeout { get; set; }
    public int? Offset { get; set; }
    public bool? Beep { get; set; }
    public int? MaxSilence { get; set; }

    public override string BuildCommand()
    {
        var cmd = string.Create(CultureInfo.InvariantCulture, $"RECORD FILE {File} {Format} {EscapeDigits} {Timeout ?? -1}");

        if (Offset.HasValue)
        {
            cmd += string.Create(CultureInfo.InvariantCulture, $" {Offset.Value}");

            if (Beep.HasValue)
            {
                cmd += Beep.Value ? " BEEP" : "";

                if (MaxSilence.HasValue)
                {
                    cmd += string.Create(CultureInfo.InvariantCulture, $" s={MaxSilence.Value}");
                }
            }
        }

        return cmd;
    }
}
