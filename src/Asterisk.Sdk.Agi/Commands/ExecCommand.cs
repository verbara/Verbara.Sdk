namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC application options</summary>
public sealed class ExecCommand : AgiCommandBase
{
    public string? Application { get; set; }
    public string? Options { get; set; }

    public override string BuildCommand() =>
        Options is not null ? $"EXEC {Application} {Options}" : $"EXEC {Application}";
}
