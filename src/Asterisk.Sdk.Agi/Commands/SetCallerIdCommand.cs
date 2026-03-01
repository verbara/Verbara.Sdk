namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET CALLERID number</summary>
public sealed class SetCallerIdCommand : AgiCommandBase
{
    public string? CallerId { get; set; }

    public override string BuildCommand() => $"SET CALLERID {CallerId}";
}
