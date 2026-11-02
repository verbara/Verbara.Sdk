using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ari.Tests.Diagnostics;

/// <summary>
/// The one state the <c>ari</c> check answers by asking Asterisk. <c>RestOnly</c> says nothing about Asterisk, so the
/// check pings it once per run under a 5 s bound: an answer is healthy and anything else is unhealthy on that run, with
/// no exception reaching the caller except the caller's own cancellation. No other state sends a request.
/// </summary>
public sealed class AriHealthCheckRestOnlyTests
{
    private const string Pong = """{"ping":"pong","timestamp":"2026-11-01T00:00:00.000+0000","asterisk_id":"a"}""";
    private const string Password = "s3cr3t-p4ss";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthyAndNameTheState_WhenAsteriskAnswersThePing()
    {
        await using var farEnd = new AriFarEnd { Respond = _ => (200, Pong) };
        await using var client = RestOnly(farEnd.BaseUrl);

        var result = await new AriHealthCheck(client).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Healthy);
            result.Data["ariState"].Should().Be("RestOnly", "a string, never the enum");
            result.Description.Should().Contain("RestOnly");
            farEnd.Requests.Should().Equal(["GET /ari/asterisk/ping HTTP/1.1"], "one run asks once");
        }
    }

    [Theory]
    [InlineData(401, "Authentication required")]
    [InlineData(500, "boom")]
    [InlineData(200, "<html>not json</html>")]
    public async Task CheckHealth_ShouldReturnUnhealthyWithTheException_WhenThePingFails(int status, string body)
    {
        await using var farEnd = new AriFarEnd { Respond = _ => (status, body) };
        await using var client = RestOnly(farEnd.BaseUrl);

        var result = await new AriHealthCheck(client).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Data["ariState"].Should().Be("RestOnly");
            result.Exception.Should().NotBeNull();
            result.Description.Should().NotContain(Password, "a health report is read by dashboards");
            result.Exception?.ToString().Should().NotContain(Password);
        }
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnUnhealthy_WhenTheTransportFails()
    {
        await using var client = RestOnly(UnreachableBaseUrl());

        var result = await new AriHealthCheck(client).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Data["ariState"].Should().Be("RestOnly");
        }
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnUnhealthyOnceTheBoundHasPassed_WhenThePingNeverAnswers()
    {
        var clock = new FakeTimeProvider();
        var asking = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = RestOnlySubstitute(async call =>
        {
            var token = call.Arg<CancellationToken>();
            asking.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token); // fence-allow: GUARD-TIMEOUT — Timeout.Infinite; the far end never answers, and only the token ends this
            return new AriAsteriskPing();
        });
        var sut = new AriHealthCheck(client) { TimeProvider = clock };

        var check = sut.CheckHealthAsync(new HealthCheckContext());
        // The token, not the task: the clock cancels it inside Advance, and the task completes later on another thread.
        var token = await asking.Task.WaitAsync(WaitLimit);
        clock.Advance(Bound - TimeSpan.FromMilliseconds(1));
        var endedBeforeTheBound = token.IsCancellationRequested;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var endedAtTheBound = token.IsCancellationRequested;
        var result = await check.WaitAsync(WaitLimit);

        using (new AssertionScope())
        {
            endedBeforeTheBound.Should().BeFalse("the check waits out its whole bound");
            endedAtTheBound.Should().BeTrue("the bound is 5 s, on the clock the check was given");
            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Data["ariState"].Should().Be("RestOnly");
        }
    }

    [Fact]
    public async Task CheckHealth_ShouldPropagate_WhenTheCallerCancels()
    {
        var clock = new FakeTimeProvider();
        var asking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = RestOnlySubstitute(async call =>
        {
            asking.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); // fence-allow: GUARD-TIMEOUT — Timeout.Infinite; the far end never answers, and only the token ends this
            return new AriAsteriskPing();
        });
        var sut = new AriHealthCheck(client) { TimeProvider = clock };
        using var caller = new CancellationTokenSource();

        var check = sut.CheckHealthAsync(new HealthCheckContext(), caller.Token);
        await asking.Task.WaitAsync(WaitLimit);
        await caller.CancelAsync();
        var outcome = async () => await check.WaitAsync(WaitLimit);

        await outcome.Should().ThrowAsync<OperationCanceledException>("a cancelled caller is not an unhealthy Asterisk");
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthyWithNoActionTaken_WhenAsteriskAnswersAgain()
    {
        var answers = true;
        await using var farEnd = new AriFarEnd { Respond = _ => Volatile.Read(ref answers) ? (200, Pong) : (503, "down") };
        await using var client = RestOnly(farEnd.BaseUrl);
        var sut = new AriHealthCheck(client);

        Volatile.Write(ref answers, false);
        var down = await sut.CheckHealthAsync(new HealthCheckContext());
        Volatile.Write(ref answers, true);
        var back = await sut.CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            down.Status.Should().Be(HealthStatus.Unhealthy);
            back.Status.Should().Be(HealthStatus.Healthy, "the next run asks again");
        }
    }

    [Theory]
    [InlineData(AriConnectionState.Initial)]
    [InlineData(AriConnectionState.Connecting)]
    [InlineData(AriConnectionState.Connected)]
    [InlineData(AriConnectionState.Reconnecting)]
    [InlineData(AriConnectionState.Disconnecting)]
    [InlineData(AriConnectionState.Disconnected)]
    [InlineData(AriConnectionState.Faulted)]
    public async Task CheckHealth_ShouldSendNoRequest_WhenTheStateIsNotRestOnly(AriConnectionState state)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(state);
        var asterisk = Substitute.For<IAriAsteriskResource>();
        client.Asterisk.Returns(asterisk);

        await new AriHealthCheck(client).CheckHealthAsync(new HealthCheckContext());

        await asterisk.DidNotReceive().PingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealth_ShouldNotChange_WhenAnOriginateGoesToAnApplicationNobodyRegistered()
    {
        await using var farEnd = new AriFarEnd
        {
            Respond = request => request.StartsWith("POST", StringComparison.Ordinal)
                ? (200, """{"id":"ch-1","name":"PJSIP/100-1","state":"Down"}""")
                : (200, Pong),
        };
        await using var client = RestOnly(farEnd.BaseUrl);
        var sut = new AriHealthCheck(client);

        var before = await sut.CheckHealthAsync(new HealthCheckContext());
        await client.Channels.OriginateAsync("PJSIP/100");
        var after = await sut.CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            before.Status.Should().Be(HealthStatus.Healthy);
            after.Status.Should().Be(HealthStatus.Healthy, "Asterisk's answer does not tell");
            client.State.Should().Be(AriConnectionState.RestOnly);
        }
    }

    private static AriClient RestOnly(string baseUrl) => new(
        Options.Create(new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = "asterisk",
            Password = Password,
            Application = "test-app",
            EventsEnabled = false,
        }),
        NullLogger<AriClient>.Instance);

    private static IAriClient RestOnlySubstitute(Func<NSubstitute.Core.CallInfo, Task<AriAsteriskPing>> ping)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(AriConnectionState.RestOnly);
        var asterisk = Substitute.For<IAriAsteriskResource>();
#pragma warning disable CA2012 // NSubstitute's setup call: the ValueTask is configured, never awaited.
        asterisk.PingAsync(Arg.Any<CancellationToken>()).Returns(call => new ValueTask<AriAsteriskPing>(ping(call)));
#pragma warning restore CA2012
        client.Asterisk.Returns(asterisk);
        return client;
    }

    /// <summary>A port nothing listens on: bound, read and released here.</summary>
    private static string UnreachableBaseUrl()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return $"http://127.0.0.1:{((System.Net.IPEndPoint)probe.LocalEndpoint).Port}";
    }
}
