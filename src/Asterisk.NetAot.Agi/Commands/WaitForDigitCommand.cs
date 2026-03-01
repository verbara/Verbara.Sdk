namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: WAIT FOR DIGIT</summary>
public sealed class WaitForDigitCommand : AgiCommandBase
{
    public long? Timeout { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "WAIT FOR DIGIT";
    }
}
