using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Exceptions;

public sealed class AmiExceptionTests
{
    [Fact]
    public void AmiConnectionException_ShouldStoreMessage()
    {
        var ex = new AmiConnectionException("connection failed");

        ex.Message.Should().Be("connection failed");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void AmiConnectionException_ShouldStoreInnerException()
    {
        var inner = new InvalidOperationException("socket error");
        var ex = new AmiConnectionException("connection failed", inner);

        ex.Message.Should().Be("connection failed");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void AmiTimeoutException_ShouldStoreMessage()
    {
        var ex = new AmiTimeoutException("timed out");

        ex.Message.Should().Be("timed out");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void AmiTimeoutException_ShouldStoreInnerException()
    {
        var inner = new TimeoutException("deadline exceeded");
        var ex = new AmiTimeoutException("action timed out", inner);

        ex.Message.Should().Be("action timed out");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void AmiConnectionException_ShouldBeAmiException()
    {
        var ex = new AmiConnectionException("test");

        ex.Should().BeAssignableTo<AmiException>();
    }

    [Fact]
    public void AmiTimeoutException_ShouldBeAmiException()
    {
        var ex = new AmiTimeoutException("test");

        ex.Should().BeAssignableTo<AmiException>();
    }
}
