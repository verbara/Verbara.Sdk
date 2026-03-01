namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: ANSWER</summary>
public sealed class AnswerCommand : AgiCommandBase
{
    public override string BuildCommand() => "ANSWER";
}
