namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Bridge;

using Verbara.Sdk.Activities;
using Verbara.Sdk.Activities.Activities;
using Verbara.Sdk.Activities.Models;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using FluentAssertions.Execution;

/// <summary>
/// A real Asterisk refusing an <c>Atxfer</c> ends <see cref="AttendedTransferActivity"/>
/// <see cref="ActivityStatus.Failed"/>, and <see cref="IActivity.StartAsync"/> throws.
/// </summary>
/// <remarks>
/// Asterisk 22 and 23 answer an <c>Atxfer</c> on a channel that does not exist with
/// <c>Response: Error</c> and <c>Message: Channel specified does not exist</c>. The test reads that
/// answer off the wire first, so the activity's side is checked against what Asterisk really sends
/// rather than against a response the test assumed.
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AttendedTransferRefusalTests : FunctionalTestBase
{
    private const string MissingChannel = "PJSIP/no-such-endpoint-00000000";
    private const string RefusalText = "Channel specified does not exist";

    public AttendedTransferRefusalTests() : base("Verbara.Sdk.Ami")
    {
    }

    [Fact]
    public async Task AttendedTransferActivity_ShouldEndFailedAndThrow_WhenAsteriskRefusesTheAtxfer()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // The producer: what Asterisk itself answers to this Atxfer.
        var raw = await connection.SendActionAsync(new AtxferAction
        {
            Channel = MissingChannel,
            Context = "test-functional",
            Exten = "161",
            Priority = 1,
        });
        raw.Response.Should().Be("Error", "Asterisk refuses an Atxfer on a channel that does not exist");
        raw.Message.Should().Be(RefusalText);

        await using var activity = new AttendedTransferActivity(connection)
        {
            Channel = MissingChannel,
            Destination = new DialPlanExtension("test-functional", "161"),
        };

        var starting = () => activity.StartAsync().AsTask();

        using var scope = new AssertionScope();
        (await starting.Should().ThrowAsync<ActivityFailedException>(
                "a transfer Asterisk refused did not happen"))
            .WithMessage($"*{RefusalText}*");
        activity.Status.Should().Be(ActivityStatus.Failed);
    }
}
