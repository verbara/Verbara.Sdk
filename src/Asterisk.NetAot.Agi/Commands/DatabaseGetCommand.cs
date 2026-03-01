namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: DATABASE GET</summary>
public sealed class DatabaseGetCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "DATABASE GET";
    }
}
