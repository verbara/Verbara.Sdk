using Verbara.Sdk;
using Verbara.Sdk.Activities.Models;
using Verbara.Sdk.Ami.Actions;

namespace Verbara.Sdk.Activities.Activities;

/// <summary>
/// Perform an attended (supervised) transfer via AMI <c>Atxfer</c>. The bridged peer on the
/// specified channel is redirected to a consultation extension, and the originating party stays
/// on the line until the consultation completes — at which point Asterisk reconnects them.
/// </summary>
/// <remarks>
/// <para>
/// Differs from <see cref="BlindTransferActivity"/>: blind transfer releases the source
/// channel immediately, while attended transfer lets the transferor consult the target before
/// completing.
/// </para>
/// <para>
/// <see cref="ActivityStatus.Completed"/> means Asterisk accepted the <c>Atxfer</c> and queued the
/// transfer (<c>Response: Success</c>, <c>Message: Atxfer successfully queued</c>); it does not mean
/// the transfer finished. When Asterisk refuses the action it answers <c>Response: Error</c> — for
/// example <c>Channel specified does not exist</c>, <c>No extension specified</c>, or
/// <c>No attended transfer feature code found</c> on a server whose <c>features.conf</c> sets no
/// <c>atxfer</c> code — and the activity then ends <see cref="ActivityStatus.Failed"/> and
/// <see cref="IActivity.StartAsync"/> throws <see cref="ActivityFailedException"/> carrying
/// Asterisk's message.
/// </para>
/// <para>
/// The transfer's outcome flows through the dialplan as AMI events — consumers wanting a
/// completion signal should subscribe to <c>AttendedTransferEvent</c> on the AMI event bus.
/// </para>
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
        var response = await Ami.SendActionAsync(action, cancellationToken);
        // Asterisk answers this action with Success (queued) or Error (refused); a refusal is an
        // ending nobody asked for, so it fails the activity like any other error.
        if (response is not null && string.Equals(response.Response, "Error", StringComparison.OrdinalIgnoreCase))
        {
            var reason = string.IsNullOrEmpty(response.Message)
                ? "Asterisk answered the Atxfer with Response: Error and no message."
                : response.Message;
            throw new ActivityFailedException(nameof(AttendedTransferActivity), reason);
        }
    }
}
