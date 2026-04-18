using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Models;
using Asterisk.Sdk.Ami.Actions;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Perform an attended (supervised) transfer via AMI <c>Atxfer</c>. The bridged peer on the
/// specified channel is redirected to a consultation extension, and the originating party stays
/// on the line until the consultation completes — at which point Asterisk reconnects them.
/// </summary>
/// <remarks>
/// Differs from <see cref="BlindTransferActivity"/>: blind transfer releases the source
/// channel immediately, while attended transfer lets the transferor consult the target before
/// completing. The transfer status flows through the dialplan via AMI events — consumers
/// wanting a completion signal should subscribe to <c>AttendedTransferEvent</c> on the
/// AMI event bus.
/// </remarks>
public sealed class AttendedTransferActivity(IAmiConnection ami) : AmiActivityBase(ami)
{
    /// <summary>Channel ID of the party whose bridged peer is being transferred (typically the customer leg).</summary>
    public required string Channel { get; init; }

    /// <summary>Consultation destination for the transfer.</summary>
    public required DialPlanExtension Destination { get; init; }

    /// <summary>Dialplan priority to start at. Defaults to 1.</summary>
    public int Priority { get; init; } = 1;

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var action = new AtxferAction
        {
            Channel = Channel,
            Context = Destination.Context,
            Exten = Destination.Extension,
            Priority = Priority,
        };
        await Ami.SendActionAsync(action, cancellationToken);
    }
}
