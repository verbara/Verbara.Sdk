using System.Globalization;

namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: WAIT FOR DIGIT timeout</summary>
public sealed class WaitForDigitCommand : AgiCommandBase
{
    public long? Timeout { get; set; }

    public override string BuildCommand() =>
        string.Create(CultureInfo.InvariantCulture, $"WAIT FOR DIGIT {Timeout ?? -1}");
}
