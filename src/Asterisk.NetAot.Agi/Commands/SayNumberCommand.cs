namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SAY NUMBER</summary>
public sealed class SayNumberCommand : AgiCommandBase
{
    public string? Number { get; set; }
    public string? EscapeDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAY NUMBER";
    }
}
