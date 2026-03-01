namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY NUMBER number escapeDigits</summary>
public sealed class SayNumberCommand : AgiCommandBase
{
    public string? Number { get; set; }
    public string? EscapeDigits { get; set; }

    public override string BuildCommand() => $"SAY NUMBER {Number} {EscapeDigits}";
}
