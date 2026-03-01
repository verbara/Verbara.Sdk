namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: NOOP</summary>
public sealed class NoopCommand : AgiCommandBase
{

    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "NOOP";
    }
}
