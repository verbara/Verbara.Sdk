namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET VARIABLE variablename</summary>
public sealed class GetVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }

    public override string BuildCommand() => $"GET VARIABLE {Variable}";
}
