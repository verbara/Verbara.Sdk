using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Asterisk.Sdk.Ami.Tests.Connection;

public sealed class AmiConnectionFactoryTests
{
    [Fact]
    public void Create_ShouldReturnAmiConnection()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);
        var options = new AmiConnectionOptions
        {
            Hostname = "192.168.1.100",
            Port = 5038,
            Username = "admin",
            Password = "secret"
        };

        var connection = sut.Create(options);

        connection.Should().NotBeNull();
        connection.Should().BeOfType<AmiConnection>();
    }

    [Fact]
    public void Create_ShouldReturnIAmiConnection()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);
        var options = new AmiConnectionOptions { Username = "user", Password = "pass" };

        var connection = sut.Create(options);

        connection.Should().BeAssignableTo<IAmiConnection>();
    }

    [Fact]
    public void Create_ShouldAcceptDifferentOptions()
    {
        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sut = new AmiConnectionFactory(socketFactory, loggerFactory);

        var conn1 = sut.Create(new AmiConnectionOptions { Hostname = "server1", Username = "u1", Password = "p1" });
        var conn2 = sut.Create(new AmiConnectionOptions { Hostname = "server2", Username = "u2", Password = "p2" });

        conn1.Should().NotBeSameAs(conn2);
    }
}
