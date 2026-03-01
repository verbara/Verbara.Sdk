namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: ASYNCAGI BREAK</summary>
public sealed class AsyncAgiBreakCommand : AgiCommandBase
{
    public override string BuildCommand() => "ASYNCAGI BREAK";
}
