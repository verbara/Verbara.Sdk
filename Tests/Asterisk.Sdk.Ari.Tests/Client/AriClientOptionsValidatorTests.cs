using Asterisk.Sdk.Ari.Client;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Tests.Client;

public sealed class AriClientOptionsValidatorTests
{
    private readonly AriClientOptionsValidator _sut = new();

    [Fact]
    public void Validate_ShouldSucceed_WhenAllFieldsAreValid()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "secret",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_ShouldFail_WhenUsernameIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "",
            Password = "secret",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPasswordIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenApplicationIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "secret",
            Application = ""
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }
}
