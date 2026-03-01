using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET AUTOHANGUP time</summary>
public sealed class SetAutoHangupCommand : AgiCommandBase
{
    public int? Time { get; set; }

    public override string BuildCommand() =>
        string.Create(CultureInfo.InvariantCulture, $"SET AUTOHANGUP {Time ?? 0}");
}
