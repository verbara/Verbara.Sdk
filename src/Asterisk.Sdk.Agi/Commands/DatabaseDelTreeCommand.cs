namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE DELTREE</summary>
public sealed class DatabaseDelTreeCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? KeyTree { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "DATABASE DELTREE";
    }
}
