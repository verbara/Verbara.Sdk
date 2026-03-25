using Asterisk.Sdk.Ari;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests;

public sealed class AriExceptionTests
{
    [Fact]
    public void AriException_ShouldStoreMessageAndStatusCode()
    {
        var ex = new AriException("Something went wrong", 500);

        ex.Message.Should().Be("Something went wrong");
        ex.StatusCode.Should().Be(500);
    }

    [Fact]
    public void AriException_ShouldAllowNullStatusCode()
    {
        var ex = new AriException("Error without status");

        ex.StatusCode.Should().BeNull();
        ex.Message.Should().Be("Error without status");
    }

    [Fact]
    public void AriException_ShouldStoreInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AriException("outer", 503, inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void AriNotFoundException_ShouldHave404StatusCode()
    {
        var ex = new AriNotFoundException("Channel not found");

        ex.StatusCode.Should().Be(404);
        ex.Message.Should().Be("Channel not found");
    }

    [Fact]
    public void AriNotFoundException_ShouldSupportInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AriNotFoundException("Not found", inner);

        ex.InnerException.Should().BeSameAs(inner);
        ex.StatusCode.Should().Be(404);
    }

    [Fact]
    public void AriConflictException_ShouldHave409StatusCode()
    {
        var ex = new AriConflictException("Bridge already exists");

        ex.StatusCode.Should().Be(409);
        ex.Message.Should().Be("Bridge already exists");
    }

    [Fact]
    public void AriConflictException_ShouldSupportInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AriConflictException("Conflict", inner);

        ex.InnerException.Should().BeSameAs(inner);
        ex.StatusCode.Should().Be(409);
    }

    [Fact]
    public void AriNotFoundException_ShouldBeAriException()
    {
        var ex = new AriNotFoundException("not found");

        ex.Should().BeAssignableTo<AriException>();
    }

    [Fact]
    public void AriConflictException_ShouldBeAriException()
    {
        var ex = new AriConflictException("conflict");

        ex.Should().BeAssignableTo<AriException>();
    }
}
