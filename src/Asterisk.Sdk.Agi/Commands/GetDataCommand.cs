namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET DATA</summary>
public sealed class GetDataCommand : AgiCommandBase
{
    public string? File { get; set; }
    public long? Timeout { get; set; }
    public int? MaxDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "GET DATA";
    }
}
