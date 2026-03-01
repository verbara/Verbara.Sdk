namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: TDD MODE on|off</summary>
public sealed class TddModeCommand : AgiCommandBase
{
    public string? Mode { get; set; }

    public override string BuildCommand() => $"TDD MODE {Mode}";
}
