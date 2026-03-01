namespace Asterisk.Sdk.Agi.Commands;

/// <summary>
/// Base class for all AGI commands.
/// </summary>
public abstract class AgiCommandBase
{
    /// <summary>Build the AGI command string to send to Asterisk.</summary>
    public abstract string BuildCommand();
}
