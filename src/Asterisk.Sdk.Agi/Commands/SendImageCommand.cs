namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SEND IMAGE image</summary>
public sealed class SendImageCommand : AgiCommandBase
{
    public string? Image { get; set; }

    public override string BuildCommand() => $"SEND IMAGE {Image}";
}
