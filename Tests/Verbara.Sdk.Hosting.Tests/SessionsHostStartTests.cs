using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// A host shaped as a consumer builds it — <c>AddVerbara</c> and then <c>AddVerbaraSessions</c> or
/// <c>AddVerbaraSessionsBuilder</c> — resolves every hosted service those registrations add, and starts and stops.
/// </summary>
/// <remarks>
/// The AMI connection is a substitute registered before <c>AddVerbara</c>, which keeps a connection already
/// registered, so the host starts with no Asterisk; every other service is the one the registrations add. The FastAGI
/// server listens on a port the OS picks.
/// </remarks>
public sealed class SessionsHostStartTests
{
    /// <summary>Bounds the host's start and stop, so a hang fails instead of stalling the lane.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public enum Registration
    {
        AddVerbaraSessions,
        AddVerbaraSessionsBuilder,
    }

    [Theory]
    [InlineData(Registration.AddVerbaraSessions)]
    [InlineData(Registration.AddVerbaraSessionsBuilder)]
    public async Task Host_ShouldResolveEveryHostedServiceAndStartAndStop_WhenBuiltWithAddVerbaraAndTheSessions(
        Registration registration)
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Connection());
                services.AddVerbara(o =>
                {
                    o.Ami.Hostname = "localhost";
                    o.Ami.Username = "admin";
                    o.Ami.Password = "secret";
                    o.AgiPort = 0;
                });
                if (registration == Registration.AddVerbaraSessions)
                    services.AddVerbaraSessions();
                else
                    services.AddVerbaraSessionsBuilder();
            })
            .Build();

        var hosted = host.Services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();
        var start = await Record.ExceptionAsync(() => host.StartAsync().WaitAsync(Bound));
        var stop = await Record.ExceptionAsync(() => host.StopAsync().WaitAsync(Bound));

        hosted.Should().Contain(
            ["SessionManagerHostedService", "SessionReconciliationService"],
            "the sessions registration adds both hosted services, and the container builds each through its public constructor");
        new Dictionary<string, Exception?> { ["Start"] = start, ["Stop"] = stop }
            .Where(step => step.Value is not null).Should().BeEmpty(
            "a host built as a consumer builds it starts and stops");
    }

    /// <summary>An established connection whose every event-generating action is answered with nothing.</summary>
    private static IAmiConnection Connection()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.9.0");
        connection.State.Returns(AmiConnectionState.Connected);
        connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => Nothing());
        return connection;
    }

    private static async IAsyncEnumerable<ManagerEvent> Nothing(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
