using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ari.Client;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ami.Tests.Connection;

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
}
