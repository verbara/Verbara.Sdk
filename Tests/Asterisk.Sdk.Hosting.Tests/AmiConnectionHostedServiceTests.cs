using Asterisk.Sdk.Hosting;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Sdk.Hosting.Tests;

public sealed class AmiConnectionHostedServiceTests
{
    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly AmiConnectionHostedService _sut;

    public AmiConnectionHostedServiceTests()
    {
        _sut = new AmiConnectionHostedService(_connection);
    }

    [Fact]
    public async Task StartAsync_ShouldCallConnectAsync()
    {
        using var cts = new CancellationTokenSource();

        await _sut.StartAsync(cts.Token);

        await _connection.Received(1).ConnectAsync(cts.Token);
    }

    [Fact]
    public async Task StopAsync_ShouldCallDisconnectAsync()
    {
        using var cts = new CancellationTokenSource();

        await _sut.StopAsync(cts.Token);

        await _connection.Received(1).DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task StopAsync_ShouldPropagateException_WhenDisconnectFails()
    {
#pragma warning disable CA2012 // NSubstitute setup requires ValueTask return in .Returns()
        _connection.DisconnectAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("disconnect failed"))));
#pragma warning restore CA2012

        var act = () => _sut.StopAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("disconnect failed");
    }

    [Fact]
    public async Task StartAsync_ShouldPropagateException_WhenConnectFails()
    {
#pragma warning disable CA2012 // NSubstitute setup requires ValueTask return in .Returns()
        _connection.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("connect failed"))));
#pragma warning restore CA2012

        var act = () => _sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("connect failed");
    }
}
