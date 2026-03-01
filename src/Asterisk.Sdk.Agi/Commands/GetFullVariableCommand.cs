namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET FULL VARIABLE variablename [channelname]</summary>
public sealed class GetFullVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }
    public string? Channel { get; set; }

    public override string BuildCommand() =>
        Channel is not null
            ? $"GET FULL VARIABLE {Variable} {Channel}"
            : $"GET FULL VARIABLE {Variable}";
}
