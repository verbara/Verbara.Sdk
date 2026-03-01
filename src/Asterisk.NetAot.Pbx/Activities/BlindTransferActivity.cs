using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Pbx.Models;

namespace Asterisk.NetAot.Pbx.Activities;

/// <summary>Perform a blind (unattended) transfer to an extension.</summary>
public sealed class BlindTransferActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required DialPlanExtension Destination { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        await Channel.SetVariableAsync("TRANSFER_CONTEXT", Destination.Context, cancellationToken);
        await Channel.ExecAsync("Goto", $"{Destination.Context},{Destination.Extension},{Destination.Priority}", cancellationToken);
    }
}
