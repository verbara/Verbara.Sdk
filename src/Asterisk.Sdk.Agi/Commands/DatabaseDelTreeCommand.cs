namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE DELTREE family [keytree]</summary>
public sealed class DatabaseDelTreeCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? KeyTree { get; set; }

    public override string BuildCommand() =>
        KeyTree is not null ? $"DATABASE DELTREE {Family} {KeyTree}" : $"DATABASE DELTREE {Family}";
}
