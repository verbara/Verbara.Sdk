using Verbara.Sdk;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// The pool registered by <c>AddVerbaraMultiServer</c> is a singleton the container disposes at shutdown. A server whose
/// disposal throws must not stop that disposal: the container disposes singletons in reverse order of creation, so an
/// exception from the pool would leave every singleton created before it undisposed and end the host's run with it.
/// </summary>
public sealed class VerbaraServerPoolHostShutdownTests
{
    [Fact]
    public async Task RunAsync_ShouldReturnAndDisposeEarlierSingletons_WhenAPoolServerFailsToDispose()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<CreatedBeforePool>();
        builder.Services.AddVerbaraMultiServer();
        // RunAsync disposes the host when it returns.
        var host = builder.Build();

        // Created first, so the container disposes it after the pool.
        var earlier = host.Services.GetRequiredService<CreatedBeforePool>();
        var pool = host.Services.GetRequiredService<VerbaraServerPool>();
        var connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(Substitute.For<IDisposable>());
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        var ari = Substitute.For<IAriClient>();
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ => throw new InvalidOperationException("ari-s1"));
#pragma warning restore CA2012
        server.SetAriClient(ari);
        pool.AddExistingServer("s1", server);

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        _ = lifetime.ApplicationStarted.Register(lifetime.StopApplication);
        var act = async () => await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await act.Should().NotThrowAsync("the host's shutdown completes when a pool server fails to dispose");
        using (new AssertionScope())
        {
            earlier.Disposed.Should().BeTrue("the singleton created before the pool is disposed after it");
            await connection.Received(1).DisposeAsync();
        }
    }

    private sealed class CreatedBeforePool : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
