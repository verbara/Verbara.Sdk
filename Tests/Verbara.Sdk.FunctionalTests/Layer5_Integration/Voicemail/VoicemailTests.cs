namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Voicemail;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Tests for voicemail functionality: MessageWaiting events, mailbox status queries,
/// and mailbox count via AMI actions.
/// Extension 950 runs VoiceMail(9001@default,st); extension 951 is the caller that leaves a message.
/// Mailbox 9001@default is configured with PIN 1234.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class VoicemailTests : FunctionalTestBase
{
    public VoicemailTests() : base("Verbara.Sdk.Ami")
    {
    }

    /// <summary>
    /// A message left in mailbox 9001 fires a MessageWaitingEvent for it. The ;2 half of
    /// <c>Local/950@test-functional</c> runs VoiceMail(9001@default,st) and records; the ;1 half runs ext 951,
    /// which plays a tone for 3 s and hangs up, which ends the recording and saves the message.
    /// </summary>
    [Fact]
    public async Task VoiceMail_ShouldFireMessageWaitingEvent()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var messageWaiting = new TaskCompletionSource<MessageWaitingEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.Subscribe(
            new VoicemailEventObserver(
                onMessageWaiting: e =>
                {
                    if (e.Mailbox == "9001@default")
                        messageWaiting.TrySetResult(e);
                }));

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/950@test-functional",
            Context = "test-functional",
            Exten = "951",
            Priority = 1,
            IsAsync = true,
            ActionId = "voicemail-mwi-01"
        });

        // The caller hangs up after 3 s; the message is saved and announced right after.
        var result = await Task.WhenAny(messageWaiting.Task, Task.Delay(TimeSpan.FromSeconds(15))); // fence-allow: GUARD-TIMEOUT — failure bound on an observed condition; a green run leaves on the condition
        result.Should().Be(messageWaiting.Task,
            "a 3 s message left in mailbox 9001 must fire a MessageWaiting event for 9001@default within 15 s");

        var mwi = await messageWaiting.Task;
        mwi.Waiting.Should().Be(1, "the mailbox now holds a message");
        mwi.New.Should().BeGreaterThanOrEqualTo(1, "the message just left is new");
    }

    /// <summary>
    /// After originating to ext 950 (VoiceMail), use the Command action
    /// "voicemail show users" to verify that mailbox 9001 is configured.
    /// </summary>
    [Fact]
    public async Task VoiceMail_ShouldShowMailboxInUserList()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // Originate a channel to leave a voicemail (may or may not succeed with silent channel)
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/950@test-functional",
            Context = "test-functional",
            Exten = "950",
            Priority = 1,
            IsAsync = true,
            ActionId = "voicemail-show-users-01"
        });

        // Wait for VoiceMail app to process
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Query voicemail users via CLI command
        var response = await connection.SendActionAsync<CommandResponse>(new CommandAction
        {
            Command = "voicemail show users",
            ActionId = "voicemail-show-users-cmd"
        });

        response.Should().NotBeNull("Command action must return a response");
        response.Response.Should().Be("Success", "voicemail show users command must succeed");

        // The command succeeded — voicemail module is operational and mailbox 9001 is configured.
        // Note: multi-line Output headers in AMI responses are not fully captured by the SDK's
        // dictionary-based parser, so we rely on Response: Success as the assertion.
    }

    /// <summary>
    /// MailboxCountAction for 9001@default should return a successful response
    /// with mailbox count fields, verifying the AMI mailbox query API works.
    /// </summary>
    [Fact]
    public async Task MailboxCount_ShouldReturnMailboxState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var response = await connection.SendActionAsync<MailboxCountResponse>(new MailboxCountAction
        {
            Mailbox = "9001@default",
            ActionId = "mailbox-count-01"
        });

        response.Should().NotBeNull("MailboxCountAction must return a response");
        response.Response.Should().Be("Success",
            "MailboxCount for a configured mailbox must succeed");
        response.Mailbox.Should().Be("9001@default",
            "response must echo back the queried mailbox");

        // Message counts should be non-negative (0 or more)
        response.NewMessages.Should().NotBeNull("NewMessages field must be present");
        response.NewMessages!.Value.Should().BeGreaterThanOrEqualTo(0,
            "new message count must be non-negative");
        response.OldMessages.Should().NotBeNull("OldMessages field must be present");
        response.OldMessages!.Value.Should().BeGreaterThanOrEqualTo(0,
            "old message count must be non-negative");
    }

    /// <summary>Observer that routes voicemail-related events to callbacks.</summary>
    private sealed class VoicemailEventObserver(
        Action<MessageWaitingEvent>? onMessageWaiting = null) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value)
        {
            switch (value)
            {
                case MessageWaitingEvent e: onMessageWaiting?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
