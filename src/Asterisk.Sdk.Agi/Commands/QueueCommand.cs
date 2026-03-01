namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC</summary>
public sealed class QueueCommand : AgiCommandBase
{

    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "EXEC";
    }
}
