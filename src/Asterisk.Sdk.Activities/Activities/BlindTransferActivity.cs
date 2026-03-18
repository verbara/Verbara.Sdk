using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Models;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Perform a blind (unattended) transfer to an extension.</summary>
public sealed class BlindTransferActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required DialPlanExtension Destination { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        await Channel.ExecAsync("BlindTransfer", $"{Destination.Extension}@{Destination.Context}", cancellationToken);
    }
}
