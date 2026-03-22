namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Security;

using Asterisk.Sdk.Ami;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AuthenticationTests : FunctionalTestBase
{
    // -----------------------------------------------------------------------
    // Test 1: Wrong password throws AmiAuthenticationException, no hang
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task Connect_ShouldFail_WithWrongPassword()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.Password = "definitely-wrong-password";
        });

        var act = async () => await connection.ConnectAsync();

        await act.Should().ThrowAsync<AmiAuthenticationException>(
            "wrong password must produce an AmiAuthenticationException");
    }

    // -----------------------------------------------------------------------
    // Test 2: Wrong username throws AmiAuthenticationException, no hang
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task Connect_ShouldFail_WithWrongUsername()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.Username = "no-such-user";
        });

        var act = async () => await connection.ConnectAsync();

        await act.Should().ThrowAsync<AmiAuthenticationException>(
            "wrong username must produce an AmiAuthenticationException");
    }

    // -----------------------------------------------------------------------
    // Test 3: Empty credentials throws AmiAuthenticationException
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task Connect_ShouldFail_WithEmptyCredentials()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.Username = string.Empty;
            opts.Password = string.Empty;
        });

        var act = async () => await connection.ConnectAsync();

        await act.Should().ThrowAsync<AmiAuthenticationException>(
            "empty credentials must produce an AmiAuthenticationException");
    }

    // -----------------------------------------------------------------------
    // Test 4: Connection to closed port throws quickly, no hang
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Connect_ShouldTimeout_WhenPortRefuses()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.Hostname = "127.0.0.1";
            opts.Port = 59999; // port that is not listening
            opts.ConnectionTimeout = TimeSpan.FromSeconds(3);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var act = async () => await connection.ConnectAsync(cts.Token);

        await act.Should().ThrowAsync<Exception>(
            "connecting to a closed port must throw rather than hang indefinitely");
    }

    // -----------------------------------------------------------------------
    // Test 5: Log entries must not contain the configured password
    // -----------------------------------------------------------------------
    [AsteriskContainerFact]
    public async Task Logs_ShouldNotContainPassword()
    {
        const string sensitivePassword = "super-secret-password-xyz987";

        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.Password = sensitivePassword;
        });

        // Attempt to connect — this will fail with wrong password, which is fine
        try
        {
            await connection.ConnectAsync();
        }
        catch (AmiAuthenticationException)
        {
            // Expected: wrong password
        }

        var allMessages = LogCapture.Entries
            .Select(e => e.Message)
            .ToList();

        allMessages.Should().NotContain(
            msg => msg.Contains(sensitivePassword, StringComparison.Ordinal),
            "log entries must never contain the plaintext password");
    }
}
