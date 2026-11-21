using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The attach probe on its own: it is marked only by the very attach it names (engine and server by reference, id by
/// ordinal), made while it is open and from the context that opened it; disposing it seals it and gives the context
/// back the probe it carried before.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class CallSessionManagerAttachProbeTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly CallSessionManager _engine = NewEngine();
    private readonly CallSessionManager _otherEngine = NewEngine();
    private readonly VerbaraServer _server = NewServer();
    private readonly VerbaraServer _otherServer = NewServer();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync().AsTask().WaitAsync(Bound);
        await _otherEngine.DisposeAsync().AsTask().WaitAsync(Bound);
        await _server.DisposeAsync().AsTask().WaitAsync(Bound);
        await _otherServer.DisposeAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public void AttachToServer_ShouldMarkTheProbe_WhenTheProbedAttachIsMadeWhileItIsOpen()
    {
        using var probe = _engine.ProbeAttach(_server, "default");

        _engine.AttachToServer(_server, "default");

        probe.Reached.Should().BeTrue("the probed attach reached the probed engine while the probe was open");
    }

    [Theory]
    [InlineData("another engine")]
    [InlineData("another server")]
    [InlineData("another id")]
    public void AttachToServer_ShouldNotMarkTheProbe_WhenTheAttachIsNotTheProbedOne(string difference)
    {
        using var probe = _engine.ProbeAttach(_server, "default");

        switch (difference)
        {
            case "another engine": _otherEngine.AttachToServer(_server, "default"); break;
            case "another server": _engine.AttachToServer(_otherServer, "default"); break;
            default: _engine.AttachToServer(_server, "Default"); break;
        }

        probe.Reached.Should().BeFalse($"an attach to {difference} is not the probed attach");
    }

    [Fact]
    public async Task AttachToServer_ShouldNotMarkTheProbe_WhenTheProbedAttachRunsAfterTheDisposeFromAContextThatCapturedIt()
    {
        using var release = new SemaphoreSlim(0);
        Task late;
        var probe = _engine.ProbeAttach(_server, "default");
        using (probe)
        {
            // Started inside the probe, so its context carries it; it attaches only after the probe is disposed.
            late = Task.Run(async () =>
            {
                await release.WaitAsync(Bound);
                _engine.AttachToServer(_server, "default");
            });
        }

        release.Release();
        await late.WaitAsync(Bound);

        probe.Reached.Should().BeFalse("the dispose sealed the probe before the late attach reached the engine");
        _engine.AttachedServer("default").Should().BeSameAs(_server, "the late attach itself still ran");
    }

    [Fact]
    public void Dispose_ShouldGiveTheContextBackItsEarlierProbe_WhenAProbeOpenedInsideAnotherIsDisposed()
    {
        using var outer = _engine.ProbeAttach(_server, "default");
        var inner = _engine.ProbeAttach(_server, "default");
        inner.Dispose();

        _engine.AttachToServer(_server, "default");

        outer.Reached.Should().BeTrue("disposing the inner probe gave the context back the outer one");
        inner.Reached.Should().BeFalse("the inner probe was disposed before the attach");
    }

    private static CallSessionManager NewEngine() =>
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());

    private static VerbaraServer NewServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("20.0.0");
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }
}
