using System.Net;
using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Agi.Tests.Server;

/// <summary>
/// The token handed to the FastAGI server's start bounds the start, not the server's life. Cancelled once the
/// start returned, or already cancelled when it began, it neither stops the accept loop nor cancels a script being
/// served, and the server stays <see cref="AgiServerState.Listening"/>. Only the server's own stop ends the loop;
/// that side is held by <see cref="FastAgiServerTests.StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped"/>.
/// </summary>
public sealed class FastAgiServerStartTokenTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The minimal AGI request header block, which is what makes a connection a script run.</summary>
    private const string AgiRequestHeaders =
        "agi_network: yes\nagi_network_script: probe\nagi_channel: SIP/test\nagi_uniqueid: 1.1\n\n";

    [Fact]
    public async Task StartAsync_ShouldKeepServing_WhenItsTokenIsCancelledAfterTheStart()
    {
        var script = new HeldScript(holds: false);
        await using var server = CreateServer(script);
        using var start = new CancellationTokenSource();
        await server.StartAsync(start.Token);

        await start.CancelAsync();
        using var peer = await SendRequestAsync(server.BoundPort);
        var served = await WithinBoundAsync(script.Reached);

        served.Should().BeTrue(
            "a FastAGI request sent after the start token was cancelled was not served: the accept loop runs on a " +
            "source linked to that token, so the cancel ended it");
        server.State.Should().Be(AgiServerState.Listening, "the server still reports itself listening, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldServe_WhenItsTokenIsAlreadyCancelled()
    {
        var script = new HeldScript(holds: false);
        await using var server = CreateServer(script);
        await server.StartAsync(new CancellationToken(canceled: true));

        using var peer = await SendRequestAsync(server.BoundPort);
        var served = await WithinBoundAsync(script.Reached);

        served.Should().BeTrue(
            "a FastAGI request was not served after a start whose token was already cancelled: the accept loop " +
            "runs on a source linked to that token, so it ended before its first accept");
        server.State.Should().Be(AgiServerState.Listening, "the server still reports itself listening, and it must also be serving");
    }

    [Fact]
    public async Task StartAsync_ShouldLetAServedScriptComplete_WhenItsTokenIsCancelledWhileTheScriptRuns()
    {
        var script = new HeldScript(holds: true);
        await using var server = CreateServer(script);
        using var start = new CancellationTokenSource();
        await server.StartAsync(start.Token);
        using var peer = await SendRequestAsync(server.BoundPort);
        (await WithinBoundAsync(script.Reached)).Should().BeTrue("premise: the script runs before the start token is cancelled");

        await start.CancelAsync();
        var cancelledByTheStartToken = script.Token.IsCancellationRequested;
        script.Release();
        var outcome = await WithinBoundAsync(script.Outcome) ? await script.Outcome : "no outcome";

        cancelledByTheStartToken.Should().BeFalse(
            "the script being served had its token cancelled by the start token's cancel: the connection's handler " +
            "runs on a source linked to that token, so a served script ends as a timeout");
        outcome.Should().Be(HeldScript.Completed, "the released script runs to its end rather than being cancelled");
        server.State.Should().Be(AgiServerState.Listening);
    }

    // ------------------------------------------------------------------------------------ helpers

    private static FastAgiServer CreateServer(IAgiScript script)
    {
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(script);
        return new FastAgiServer(0, strategy, NullLogger<FastAgiServer>.Instance);
    }

    /// <summary>Connects on the IPv4 loopback literal and sends a complete AGI request.</summary>
    private static async Task<TcpClient> SendRequestAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(AgiRequestHeaders));
        await client.GetStream().FlushAsync();
        return client;
    }

    /// <summary>
    /// Whether <paramref name="signal"/> completes within the bound. Returning a bool rather than letting the timeout
    /// escape turns "never served" into an assertion failure carrying the reason instead of a bare timeout.
    /// </summary>
    private static async Task<bool> WithinBoundAsync(Task signal)
    {
        try
        {
            await signal.WaitAsync(SignalTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// A script that reports it ran and keeps the token it was handed. Holding, it then waits for the test's release
    /// on that token, and reports whether it ran to its end or was cancelled first.
    /// </summary>
    private sealed class HeldScript(bool holds) : IAgiScript
    {
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public Task<string> Outcome => _outcome.Task;

        public CancellationToken Token { get; private set; }

        public void Release() => _release.TrySetResult();

        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            _reached.TrySetResult();
            if (!holds)
                return;

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                _outcome.TrySetResult(Completed);
            }
            catch (OperationCanceledException)
            {
                _outcome.TrySetResult(Cancelled);
                throw;
            }
        }
    }
}
