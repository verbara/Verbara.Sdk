namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET EXTENSION extension</summary>
public sealed class SetExtensionCommand : AgiCommandBase
{
    public string? Extension { get; set; }

    public override string BuildCommand() => $"SET EXTENSION {Extension}";
}
