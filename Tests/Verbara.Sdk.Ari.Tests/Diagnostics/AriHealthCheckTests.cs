using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ari.Tests.Diagnostics;

/// <summary>
/// The <c>ari</c> health check reads the client's state with the table the AMI connection's check uses:
/// <c>Connected</c> is healthy; <c>Initial</c>, <c>Connecting</c> and <c>Reconnecting</c> are degraded, because
/// the client is not connected but nothing has ended it; <c>Disconnecting</c>, <c>Disconnected</c> and ARI's own
/// <c>Faulted</c> are unhealthy, because nothing reconnects the client until a new connect.
/// </summary>
public sealed class AriHealthCheckTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenConnected()
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(AriConnectionState.Connected);
        var sut = new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenReconnecting()
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(AriConnectionState.Reconnecting);
        var sut = new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    /// <summary>
    /// The states nothing reconnects from until a new connect. <c>Initial</c> is not one of them: nobody has
    /// connected the client yet, which is not a failure.
    /// </summary>
    [Theory]
    [InlineData(AriConnectionState.Disconnecting)]
    [InlineData(AriConnectionState.Disconnected)]
    [InlineData(AriConnectionState.Faulted)]
    public async Task CheckHealth_ShouldReturnUnhealthy_WhenNothingReconnectsTheClient(AriConnectionState state)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(state);
        var sut = new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Theory]
    [InlineData(AriConnectionState.Initial, HealthStatus.Degraded)]
    [InlineData(AriConnectionState.Connecting, HealthStatus.Degraded)]
    [InlineData(AriConnectionState.Connected, HealthStatus.Healthy)]
    [InlineData(AriConnectionState.Reconnecting, HealthStatus.Degraded)]
    [InlineData(AriConnectionState.Disconnecting, HealthStatus.Unhealthy)]
    [InlineData(AriConnectionState.Disconnected, HealthStatus.Unhealthy)]
    [InlineData(AriConnectionState.Faulted, HealthStatus.Unhealthy)]
    public async Task CheckHealth_ShouldMapEveryStateByTheTable(AriConnectionState state, HealthStatus expected)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(state);
        var sut = new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(expected, $"{state} maps to {expected}");
    }

    [Theory]
    [InlineData(AriConnectionState.Initial)]
    [InlineData(AriConnectionState.Connecting)]
    [InlineData(AriConnectionState.Connected)]
    [InlineData(AriConnectionState.Reconnecting)]
    [InlineData(AriConnectionState.Disconnecting)]
    [InlineData(AriConnectionState.Disconnected)]
    [InlineData(AriConnectionState.Faulted)]
    public async Task CheckHealth_ShouldCarryTheStateNameAsAString_InEveryState(AriConnectionState state)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(state);
        var sut = new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Data.Should().ContainKey("ariState");
        if (result.Data.TryGetValue("ariState", out var value))
            value.Should().BeOfType<string>("a reflection-free JSON writer cannot serialize a boxed enum")
                .Which.Should().Be(state.ToString());
        result.Description.Should().Contain(state.ToString(), "the description names the state");
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenAConnectIsInProgress()
    {
        // A real client whose dial the far end accepts and never answers: the check runs mid-connect.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
        }), NullLogger<AriClient>.Instance);

        using var caller = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var silence = Task.Run(async () =>
        {
            using var held = await server.AcceptTcpClientAsync();
            accepted.SetResult();
            await release.Task;
        });

        var connect = sut.ConnectAsync(caller.Token).AsTask();
        try
        {
            await accepted.Task.WaitAsync(WaitLimit);
            var result = await new Verbara.Sdk.Ari.Diagnostics.AriHealthCheck(sut).CheckHealthAsync(new HealthCheckContext());

            sut.State.Should().Be(AriConnectionState.Connecting, "the dial is held");
            result.Status.Should().Be(HealthStatus.Degraded, "a connect in progress is not a failure");
        }
        finally
        {
            await caller.CancelAsync();
            try
            {
                await connect.WaitAsync(WaitLimit);
            }
            catch (OperationCanceledException)
            {
                // The withdrawal this test asked for.
            }

            release.TrySetResult();
            await silence.WaitAsync(WaitLimit).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
