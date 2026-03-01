using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY TIME time escapeDigits</summary>
public sealed class SayTimeCommand : AgiCommandBase
{
    public long? Time { get; set; }
    public string? EscapeDigits { get; set; }

    public override string BuildCommand() =>
        string.Create(CultureInfo.InvariantCulture, $"SAY TIME {Time ?? 0} {EscapeDigits}");
}
