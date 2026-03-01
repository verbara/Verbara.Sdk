using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: RECEIVE CHAR [timeout]</summary>
public sealed class ReceiveCharCommand : AgiCommandBase
{
    public int? Timeout { get; set; }

    public override string BuildCommand() =>
        Timeout.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"RECEIVE CHAR {Timeout.Value}")
            : "RECEIVE CHAR";
}
