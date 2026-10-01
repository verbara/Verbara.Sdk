namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.EventDelivery;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="VerbaraServer.OriginateAsync"/> against a real Asterisk. Asterisk answers an async originate with
/// <c>Response: Success</c> and then sends one <c>OriginateResponse</c> event that carries the originate's ActionID and,
/// in a <c>Response</c> header, its outcome. The connection took that event for a second response and dropped it, so
/// every originate Asterisk accepted threw <see cref="OperationCanceledException"/> at the connection's
/// <c>DefaultEventTimeout</c>: 60 of 60 on Asterisk 20.20.1, 22.9.0 and 23.4.1. The timeout is left at its 5 s
/// default here, as a caller leaves it.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class OriginateOutcomeTests : FunctionalTestBase
{
    /// <summary><c>Answer()</c>, <c>Wait(30)</c>: a destination that answers at once.</summary>
    private const string AnsweringDestination = "Local/100@test-functional";

    private const string HangupEverything = "channel request hangup all";

    /// <summary>A hang bound, far above the connection's own 5 s event timeout.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public OriginateOutcomeTests() : base("Verbara.Sdk.Live")
    {
    }

    [Fact]
    public async Task OriginateAsync_ShouldReturnSuccess_WhenTheDestinationAnswers()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();
        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());

        try
        {
            OriginateResult? result = null;
            var error = await Record.ExceptionAsync(async () => result = await server
                .OriginateAsync(AnsweringDestination, "test-functional", "100", timeout: TimeSpan.FromSeconds(20))
                .AsTask().WaitAsync(Bound));

            using (new AssertionScope())
            {
                error.Should().BeNull("Asterisk accepted the originate and reported its outcome in an OriginateResponse");
                result?.Success.Should().BeTrue("the destination answered");
                result?.Message.Should().Be("Success");
                result?.ChannelId.Should().StartWith("Local/100@test-functional", "the outcome names the originated channel");
            }
        }
        finally
        {
            await BestEffort.SendAsync(connection, new CommandAction { Command = HangupEverything });
        }
    }

    [Fact]
    public async Task OriginateAsync_ShouldReturnFailure_WhenTheChannelTechnologyIsMissing()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();
        await using var server = new VerbaraServer(connection, LoggerFactory.CreateLogger<VerbaraServer>());

        OriginateResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await server
            .OriginateAsync("Nosuchtech/x", "test-functional", "100", timeout: TimeSpan.FromSeconds(20))
            .AsTask().WaitAsync(Bound));

        using (new AssertionScope())
        {
            error.Should().BeNull(
                "Asterisk accepts an originate to a technology it does not have and reports the failure in an OriginateResponse");
            result?.Success.Should().BeFalse();
            result?.Message.Should().Be("Failure");
        }
    }
}
