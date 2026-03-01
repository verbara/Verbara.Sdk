namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: VERBOSE</summary>
public sealed class VerboseCommand : AgiCommandBase
{
    public string? Message { get; set; }
    public int? Level { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "VERBOSE";
    }
}
