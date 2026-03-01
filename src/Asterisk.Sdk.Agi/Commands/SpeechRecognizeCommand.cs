using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH RECOGNIZE prompt timeout [offset]</summary>
public sealed class SpeechRecognizeCommand : AgiCommandBase
{
    public string? Prompt { get; set; }
    public int? Timeout { get; set; }
    public int? Offset { get; set; }

    public override string BuildCommand()
    {
        var cmd = string.Create(CultureInfo.InvariantCulture, $"SPEECH RECOGNIZE {Prompt} {Timeout ?? 0}");

        if (Offset.HasValue)
        {
            cmd += string.Create(CultureInfo.InvariantCulture, $" {Offset.Value}");
        }

        return cmd;
    }
}
