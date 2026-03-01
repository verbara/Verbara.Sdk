namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET VARIABLE variablename value</summary>
public sealed class SetVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }
    public string? Value { get; set; }

    public override string BuildCommand() => $"SET VARIABLE {Variable} {Value}";
}
