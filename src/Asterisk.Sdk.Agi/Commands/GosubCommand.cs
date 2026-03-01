namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GOSUB context extension priority [optional-arg]</summary>
public sealed class GosubCommand : AgiCommandBase
{
    public string? Context { get; set; }
    public string? Extension { get; set; }
    public string? Priority { get; set; }
    public string? OptionalArg { get; set; }

    public override string BuildCommand() =>
        OptionalArg is not null
            ? $"GOSUB {Context} {Extension} {Priority} {OptionalArg}"
            : $"GOSUB {Context} {Extension} {Priority}";
}
