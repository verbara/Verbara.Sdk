using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: RECEIVE TEXT [timeout]</summary>
public sealed class ReceiveTextCommand : AgiCommandBase
{
    public int? Timeout { get; set; }

    public override string BuildCommand() =>
        Timeout.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"RECEIVE TEXT {Timeout.Value}")
            : "RECEIVE TEXT";
}
