namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: AGI command args — executes a sub-AGI script.</summary>
public sealed class AgiCommand : AgiCommandBase
{
    public string? Command { get; set; }
    public string? Args { get; set; }

    public override string BuildCommand() =>
        Args is not null ? $"AGI {Command} {Args}" : $"AGI {Command}";
}
