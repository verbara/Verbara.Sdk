namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET CONTEXT context</summary>
public sealed class SetContextCommand : AgiCommandBase
{
    public string? Context { get; set; }

    public override string BuildCommand() => $"SET CONTEXT {Context}";
}
