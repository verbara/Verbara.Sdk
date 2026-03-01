namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: TDD MODE</summary>
public sealed class TddModeCommand : AgiCommandBase
{
    public string? Mode { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "TDD MODE";
    }
}
