using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// What <c>AddVerbaraAri</c> registers: the ARI client, its validated options, its <c>ari</c> check, its hosted service
/// and, with the audio-server callback set, the audio servers; nothing of AMI, AGI or Live. And that the ARI client is
/// registered once, whichever of <c>AddVerbara</c> and <c>AddVerbaraAri</c> registers it.
/// </summary>
public sealed class AddVerbaraAriTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AddVerbaraAri_ShouldRegisterOnlyAri_WhenCalledAlone()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddVerbaraAri(Valid);

        await using var provider = services.BuildServiceProvider();
        var names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name);
        using (new AssertionScope())
        {
            provider.GetService<IAriClient>().Should().BeOfType<AriClient>("the ARI client is what this registration is for");
            names.Should().Equal(["ari"], "an ARI-only host gets the ari check and no ami, agi or live check");
            services.Should().NotContain(d => d.ServiceType == typeof(IAmiConnection), "nothing of AMI is registered");
            services.Should().NotContain(d => d.ServiceType == typeof(IAgiServer), "no AGI server is registered");
            services.Should().NotContain(d => d.ServiceType == typeof(IVerbaraServer), "no Live server is registered");
            services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType)
                .Where(t => t?.Namespace?.StartsWith("Verbara.", StringComparison.Ordinal) == true)
                .Should().Equal([typeof(AriConnectionHostedService)],
                    "the SDK's one hosted service is the one that connects the client (the health-check publisher is the framework's)");
            provider.GetRequiredService<IAriClient>().AudioServer.Should().BeNull("no audio-server callback was set");
        }
    }

    [Fact]
    public async Task AddVerbaraAri_ShouldRegisterTheAudioServers_WhenTheCallbackIsSet()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddVerbaraAri(o =>
        {
            Valid(o);
            o.ConfigureAudioServer = a =>
            {
                a.AudioSocketPort = 9191;
                a.WebSocketPort = 9192;
            };
        });

        await using var provider = services.BuildServiceProvider();
        var audioServer = provider.GetRequiredService<IAudioServer>();
        using (new AssertionScope())
        {
            provider.GetRequiredService<AudioServerOptions>().AudioSocketPort.Should().Be(9191, "the callback configures the audio servers");
            provider.GetService<AudioSocketServer>().Should().NotBeNull("the AudioSocket server is registered");
            provider.GetService<WebSocketAudioServer>().Should().NotBeNull("a WebSocket port above 0 registers the WebSocket server");
            audioServer.Should().BeOfType<CompositeAudioServer>("both servers are handed out as one");
            provider.GetRequiredService<IAriClient>().AudioServer.Should().BeSameAs(audioServer, "the client is built with the audio server");
            services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType)
                .Should().Contain(typeof(AriAudioHostedService), "the audio servers start with the host");
        }
    }

    [Fact]
    public async Task AddVerbaraAri_ShouldFailTheStart_WhenApplicationIsMissing()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddVerbaraAri(o =>
            {
                o.BaseUrl = UnreachableBaseUrl();
                o.Username = "asterisk";
                o.Password = "secret";
                o.EventsEnabled = false;
            }))
            .Build();

        var start = () => host.StartAsync().WaitAsync(Bound);

        (await start.Should().ThrowAsync<OptionsValidationException>("the options are validated on start, as through AddVerbara"))
            .Which.Message.Should().Contain(nameof(AriClientOptions.Application), "the failure names the option to fix");
    }

    [Fact]
    public void AddVerbaraAri_ShouldThrow_WhenAddVerbaraRegisteredAri()
    {
        var services = new ServiceCollection();
        services.AddVerbara(o => o.Ari = new AriClientOptions());

        var act = () => services.AddVerbaraAri(Valid);

        ShouldNameBothMethods(act);
        services.Count(d => d.ServiceType == typeof(IAriClient)).Should().Be(1, "the failing call registered no second client");
    }

    [Fact]
    public void AddVerbara_ShouldThrow_WhenAddVerbaraAriRanFirst()
    {
        var services = new ServiceCollection();
        services.AddVerbaraAri(Valid);

        var act = () => services.AddVerbara(o => o.Ari = new AriClientOptions());

        ShouldNameBothMethods(act);
        services.Count(d => d.ServiceType == typeof(IAriClient)).Should().Be(1, "the failing call registered no second client");
    }

    [Fact]
    public void AddVerbaraAri_ShouldThrow_WhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddVerbaraAri(Valid);

        var act = () => services.AddVerbaraAri(Valid);

        ShouldNameBothMethods(act);
        services.Count(d => d.ServiceType == typeof(IAriClient)).Should().Be(1, "the failing call registered no second client");
    }

    [Fact]
    public async Task AddVerbaraAri_ShouldRegisterOneAriCheck_WhenAddVerbaraHasNoAri()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(_ => { });

        var act = () => services.AddVerbaraAri(Valid);

        act.Should().NotThrow("AddVerbara with no ARI options registered no ARI client");
        await using var provider = services.BuildServiceProvider();
        var names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name);
        using (new AssertionScope())
        {
            names.Where(n => n == "ari").Should().ContainSingle("the ARI client is registered once");
            provider.Invoking(p => p.GetRequiredService<HealthCheckService>())
                .Should().NotThrow("no check name is registered twice, so the health check service resolves");
        }
    }

    [Fact]
    public async Task AddVerbaraAri_ShouldTagTheAriCheck_WhenTheHostNamesTags()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraAri(Valid);
        services.Configure<VerbaraHealthCheckOptions>(o => o.Tags.Add("voice"));

        await using var provider = services.BuildServiceProvider();
        var ari = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Should().ContainSingle("an ARI-only host has the ari check alone").Which;

        ari.Name.Should().Be("ari");
        ari.Tags.Should().BeEquivalentTo(["voice"], "the ARI-only registration reads the same host-wide option");
        ari.Factory(provider).Should().BeOfType<Verbara.Sdk.Ari.Diagnostics.AriHealthCheck>("the check itself is unchanged");
    }

    private static void ShouldNameBothMethods(Func<IServiceCollection> act)
    {
        var message = act.Should().Throw<InvalidOperationException>("the ARI client is registered once").Which.Message;
        message.Should().Contain("AddVerbara(").And.Contain("AddVerbaraAri(", "the failure names both ways to register it");
    }

    private static void Valid(AriClientOptions o)
    {
        o.BaseUrl = "http://127.0.0.1:8088";
        o.Username = "asterisk";
        o.Password = "secret";
        o.Application = "ari-only";
        o.EventsEnabled = false;
    }

    /// <summary>A port nothing listens on: bound, read and released here.</summary>
    private static string UnreachableBaseUrl()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
    }
}
