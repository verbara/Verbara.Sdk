namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE PUT</summary>
public sealed class DatabasePutCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "DATABASE PUT";
    }
}
