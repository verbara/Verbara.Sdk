namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: DATABASE DEL family key</summary>
public sealed class DatabaseDelCommand : AgiCommandBase
{
    public string? Family { get; set; }
    public string? KeyTree { get; set; }

    public override string BuildCommand() => $"DATABASE DEL {Family} {KeyTree}";
}
