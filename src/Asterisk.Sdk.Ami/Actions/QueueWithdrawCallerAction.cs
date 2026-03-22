using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

/// <summary>
/// Withdraws a caller from a queue. Asterisk 20+.
/// The caller is removed from the queue and the call continues in the dialplan.
/// </summary>
[AsteriskMapping("QueueWithdrawCaller")]
public sealed class QueueWithdrawCallerAction : ManagerAction
{
    /// <summary>The queue name to withdraw the caller from.</summary>
    public string? Queue { get; set; }
    /// <summary>The channel of the caller to withdraw.</summary>
    public string? Caller { get; set; }
}
