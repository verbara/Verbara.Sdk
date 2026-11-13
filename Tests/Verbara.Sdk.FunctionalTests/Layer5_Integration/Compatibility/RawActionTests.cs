namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Compatibility;

using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;
using AmiConnection = Verbara.Sdk.Ami.Connection.AmiConnection;
using EventActionOutcome = Verbara.Sdk.Ami.Connection.EventActionOutcome;

/// <summary>
/// An action the SDK does not type, sent by name as a <see cref="RawAction"/> to the pinned image, is accepted and
/// answered as Asterisk answers it, so every merge-group lane keeps that premise measured on its version.
/// </summary>
/// <remarks>
/// <c>FAXSessions</c> is the listing case, not <c>IAXpeers</c>: the functional configuration has no <c>iax.conf</c>, so
/// the image registers no <c>IAX*</c> action. <c>VerbaraNoSuchAction</c> stays as the case that shows a refusal is
/// told apart from an acceptance.
/// </remarks>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class RawActionTests : FunctionalTestBase
{
    [Fact]
    public async Task SendActionAsync_ShouldBeAnsweredSuccess_WhenRawLoggerRotateIsSent()
    {
        await using var connection = await ConnectBootedAsync();

        var response = await connection.SendActionAsync(new RawAction("LoggerRotate"));

        response.Response.Should().Be("Success", $"Asterisk registers LoggerRotate; it answered '{response.Message}'");
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldReportCompletionWithoutRejection_WhenRawFaxSessionsIsSent()
    {
        await using var connection = await ConnectBootedAsync();
        ((IAmiConnection)connection).ReportsEventActionOutcome.Should().BeTrue();
        var outcome = new EventActionOutcome();

        await foreach (var _ in connection.SendEventGeneratingActionAsync(new RawAction("FAXSessions"), outcome))
        {
            // Entries, if any session is up, are not the subject: how the sequence ended is.
        }

        outcome.Reported.Should().BeTrue("the SDK's connection reports how an event-generating action ended");
        outcome.Rejection.Should().BeNull("Asterisk registers FAXSessions and answers it Success");
        outcome.SessionEnded.Should().BeFalse("the sequence ended at FAXSessionsComplete, not at the session's end");
    }

    [Fact]
    public async Task SendActionAsync_ShouldReturnAsterisksError_WhenRawActionNameIsNotRegistered()
    {
        await using var connection = await ConnectBootedAsync();

        var response = await connection.SendActionAsync(new RawAction("VerbaraNoSuchAction"));

        response.Response.Should().Be("Error");
        response.Message.Should().Contain("Invalid/unknown command");
    }

    /// <summary>A connection on an Asterisk that has loaded every module, so every action is registered.</summary>
    private async Task<AmiConnection> ConnectBootedAsync()
    {
        var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(60);
            opts.DefaultEventTimeout = TimeSpan.FromSeconds(30);
            opts.AutoReconnect = false;
        });
        try
        {
            await connection.ConnectAsync();
            await connection.SendActionAsync<CommandResponse>(new CommandAction { Command = "core waitfullybooted" });
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
