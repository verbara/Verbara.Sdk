using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Hosting;
using FluentAssertions;
using NSubstitute;

#pragma warning disable CA2012 // Use ValueTasks correctly — NSubstitute setup requires this pattern

namespace Asterisk.Sdk.Agi.Tests.Hosting;

public class AgiHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldDelegateToAgiServer()
    {
        var server = Substitute.For<IAgiServer>();
        server.StartAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var hostedService = new AgiHostedService(server);

        await hostedService.StartAsync(CancellationToken.None);

        await server.Received(1).StartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_ShouldDelegateToAgiServer()
    {
        var server = Substitute.For<IAgiServer>();
        server.StopAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var hostedService = new AgiHostedService(server);

        await hostedService.StopAsync(CancellationToken.None);

        await server.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ShouldPassCancellationToken()
    {
        var server = Substitute.For<IAgiServer>();
        server.StartAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var hostedService = new AgiHostedService(server);
        using var cts = new CancellationTokenSource();

        await hostedService.StartAsync(cts.Token);

        await server.Received(1).StartAsync(cts.Token);
    }
}

#pragma warning restore CA2012
