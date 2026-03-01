namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SAY TIME</summary>
public sealed class SayTimeCommand : AgiCommandBase
{
    public long? Time { get; set; }
    public string? EscapeDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAY TIME";
    }
}
