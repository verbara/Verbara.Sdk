namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: EXEC</summary>
public sealed class ExecCommand : AgiCommandBase
{
    public string? Application { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "EXEC";
    }
}
