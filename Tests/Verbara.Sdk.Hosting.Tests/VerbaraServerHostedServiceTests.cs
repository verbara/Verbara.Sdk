using Verbara.Sdk.Hosting;
using FluentAssertions;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

public sealed class VerbaraServerHostedServiceTests
{
    private readonly IVerbaraServer _server = Substitute.For<IVerbaraServer>();
    private readonly VerbaraServerHostedService _sut;

    public VerbaraServerHostedServiceTests()
    {
        _sut = new VerbaraServerHostedService(_server);
    }

    [Fact]
    public async Task StartAsync_ShouldCallServerStartAsync()
    {
        using var cts = new CancellationTokenSource();

        await _sut.StartAsync(cts.Token);

        await _server.Received(1).StartAsync(cts.Token);
    }

    [Fact]
    public async Task StartAsync_ShouldPropagateException_WhenServerStartFails()
    {
        _server.StartAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("start failed")));

        var act = () => _sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("start failed");
    }

    [Fact]
    public async Task StopAsync_ShouldReturnCompletedTask()
    {
        var result = _sut.StopAsync(CancellationToken.None);

        result.IsCompleted.Should().BeTrue();
        await result;
    }

    [Fact]
    public async Task StopAsync_ShouldNotCallServer()
    {
        await _sut.StopAsync(CancellationToken.None);

        // StopAsync returns Task.CompletedTask and does not interact with the server
        await _server.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
    }
}
