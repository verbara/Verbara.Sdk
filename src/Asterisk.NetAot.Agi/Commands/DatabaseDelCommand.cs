namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: DATABASE DEL</summary>
public sealed class DatabaseDelCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? KeyTree { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "DATABASE DEL";
    }
}
