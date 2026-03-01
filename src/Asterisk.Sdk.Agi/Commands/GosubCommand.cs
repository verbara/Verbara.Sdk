namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GOSUB</summary>
public sealed class GosubCommand : AgiCommandBase
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public string? Priority { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "GOSUB";
    }
}
