using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

public class AmiConnectionOptionsValidatorTests
{
    private readonly AmiConnectionOptionsValidator _validator = new();

    [Fact]
    public void Validate_ShouldSucceed_WithValidOptions()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5038,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenUsernameEmpty()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPasswordEmpty()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = ""
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPortOutOfRange()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 0,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenHostnameNull()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = null!,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void AriValidator_ShouldFail_WhenBaseUrlEmpty()
    {
        var ariValidator = new AriClientOptionsValidator();
        var options = new AriClientOptions
        {
            BaseUrl = "",
            Username = "admin",
            Password = "secret",
            Application = "testapp"
        };

        var result = ariValidator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    /// <summary>
    /// The recorded defaults: a heartbeat every 30 s with a 10 s timeout, enabled (ADR-0021), and a
    /// reconnect that starts at 1 s, doubles, stops growing at 30 s and never gives up (ADR-0008).
    /// </summary>
    [Fact]
    public void Constructor_ShouldUseTheRecordedHeartbeatAndReconnectDefaults_WhenNothingIsSet()
    {
        var options = new AmiConnectionOptions();

        options.Should().BeEquivalentTo(new
        {
            EnableHeartbeat = true,
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromSeconds(1),
            ReconnectMultiplier = 2.0,
            ReconnectMaxDelay = TimeSpan.FromSeconds(30),
            MaxReconnectAttempts = 0,
        });
    }
}
