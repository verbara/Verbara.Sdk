using System.Globalization;
using Verbara.Sdk;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Outbound;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Extension methods for registering Verbara Sdk services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Verbara Sdk services (AMI, AGI, ARI, Live) to the service collection.
    /// Configures a single Asterisk server connection with options validation on startup.
    /// </summary>
    public static IServiceCollection AddVerbara(
        this IServiceCollection services,
        Action<VerbaraOptions> configure)
    {
        var options = new VerbaraOptions();
        configure(options);

        // Transport
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();

        // AMI (single-server) with AOT-safe source-generated validation
        // Every option is copied, so a value set here replaces one a Configure<T> placed before AddVerbara set,
        // and a Configure<T> placed after it adjusts what AddVerbara set.
        services.AddOptions<AmiConnectionOptions>()
            .Configure(o => CopyAmiOptions(options.Ami, o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AmiConnectionOptions>, AmiConnectionOptionsValidator>();
        services.TryAddSingleton<IAmiConnection, AmiConnection>();

        // AMI Factory (always available for creating additional connections)
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();

        // AGI
        var mappingStrategy = options.AgiMappingStrategy ?? new SimpleMappingStrategy();
        services.TryAddSingleton<IMappingStrategy>(mappingStrategy);
        services.TryAddSingleton<IAgiServer>(sp =>
            new FastAgiServer(
                options.AgiPort,
                sp.GetRequiredService<IMappingStrategy>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FastAgiServer>>()));
        services.AddHealthChecks()
            .AddCheck<Verbara.Sdk.Agi.Diagnostics.AgiHealthCheck>("agi");
        services.AddSingleton<IHostedService, Verbara.Sdk.Agi.Hosting.AgiHostedService>();

        // Live
        services.TryAddSingleton<VerbaraServer>();
        services.TryAddSingleton<IVerbaraServer>(sp => sp.GetRequiredService<VerbaraServer>());
        services.AddHealthChecks()
            .AddCheck<Verbara.Sdk.Live.Diagnostics.LiveHealthCheck>("live");

        // Hosted services for automatic lifecycle management
        services.AddSingleton<IHostedService, AmiConnectionHostedService>();
        services.AddSingleton<IHostedService, VerbaraServerHostedService>();

        // Health checks
        services.AddHealthChecks()
            .AddCheck<Verbara.Sdk.Ami.Diagnostics.AmiHealthCheck>("ami");

        // ARI with validation
        if (options.Ari is not null)
        {
            var ariOptions = options.Ari;
            services.AddOptions<AriClientOptions>()
                .Configure(o => CopyAriOptions(ariOptions, o))
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<AriClientOptions>, AriClientOptionsValidator>();

            // Audio servers (AudioSocket + WebSocket)
            if (options.Ari.ConfigureAudioServer is not null)
            {
                var audioOpts = new AudioServerOptions();
                options.Ari.ConfigureAudioServer(audioOpts);
                services.AddSingleton(audioOpts);
                services.TryAddSingleton<AudioSocketServer>();

                if (audioOpts.WebSocketPort > 0)
                    services.TryAddSingleton<WebSocketAudioServer>();

                services.TryAddSingleton<IAudioServer>(sp =>
                {
                    var servers = new List<IAudioServer> { sp.GetRequiredService<AudioSocketServer>() };
                    var ws = sp.GetService<WebSocketAudioServer>();
                    if (ws is not null) servers.Add(ws);
                    return new CompositeAudioServer(servers);
                });

                services.AddSingleton<IHostedService, AriAudioHostedService>();
            }

            // Use factory to safely inject optional IAudioServer
            services.TryAddSingleton<IAriClient>(sp => new AriClient(
                sp.GetRequiredService<IOptions<AriClientOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AriClient>>(),
                sp.GetService<IAudioServer>()));

            services.AddHealthChecks()
                .AddCheck<Verbara.Sdk.Ari.Diagnostics.AriHealthCheck>("ari");
            services.AddSingleton<IHostedService, AriConnectionHostedService>();
        }

        return services;
    }

    /// <summary>
    /// Add all Verbara Sdk services binding options from <see cref="IConfiguration"/>.
    /// Binds every option of <see cref="AmiConnectionOptions"/> from the <c>Asterisk:Ami</c> section and of
    /// <see cref="AriClientOptions"/> from the <c>Asterisk:Ari</c> section (the ARI client is registered only when that
    /// section exists), and the AGI port from <c>Asterisk:AgiPort</c>. Values are parsed with the invariant culture, and
    /// a value that cannot be converted throws an <see cref="InvalidOperationException"/> naming its key.
    /// AOT-safe: the binding is emitted by the configuration-binding source generator, not done by reflection.
    /// </summary>
    public static IServiceCollection AddVerbara(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var ami = configuration.GetSection("Asterisk:Ami");
        var ari = configuration.GetSection("Asterisk:Ari");

        return services.AddVerbara(o =>
        {
            ami.Bind(o.Ami);

            if (int.TryParse(configuration["Asterisk:AgiPort"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var agiPort))
                o.AgiPort = agiPort;

            if (ari.Exists())
            {
                o.Ari = new AriClientOptions();
                ari.Bind(o.Ari);
            }
        });
    }

    /// <summary>Copies every settable option of <paramref name="source"/> onto <paramref name="target"/>.</summary>
    private static void CopyAmiOptions(AmiConnectionOptions source, AmiConnectionOptions target)
    {
        target.Hostname = source.Hostname;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Password = source.Password;
        target.UseSsl = source.UseSsl;
        target.ConnectionTimeout = source.ConnectionTimeout;
        target.ReadTimeout = source.ReadTimeout;
        target.DefaultResponseTimeout = source.DefaultResponseTimeout;
        target.DefaultEventTimeout = source.DefaultEventTimeout;
        target.AutoReconnect = source.AutoReconnect;
        target.MaxReconnectAttempts = source.MaxReconnectAttempts;
        target.EventPumpCapacity = source.EventPumpCapacity;
        target.ReconnectInitialDelay = source.ReconnectInitialDelay;
        target.ReconnectMaxDelay = source.ReconnectMaxDelay;
        target.ReconnectMultiplier = source.ReconnectMultiplier;
        target.EnableHeartbeat = source.EnableHeartbeat;
        target.HeartbeatInterval = source.HeartbeatInterval;
        target.HeartbeatTimeout = source.HeartbeatTimeout;
    }

    /// <summary>Copies every settable option of <paramref name="source"/> onto <paramref name="target"/>, the audio-server callback included.</summary>
    private static void CopyAriOptions(AriClientOptions source, AriClientOptions target)
    {
        target.ConfigureAudioServer = source.ConfigureAudioServer;
        target.BaseUrl = source.BaseUrl;
        target.Username = source.Username;
        target.Password = source.Password;
        target.Application = source.Application;
        target.AutoReconnect = source.AutoReconnect;
        target.ReconnectInitialDelay = source.ReconnectInitialDelay;
        target.ReconnectMaxDelay = source.ReconnectMaxDelay;
        target.ReconnectMultiplier = source.ReconnectMultiplier;
        target.MaxReconnectAttempts = source.MaxReconnectAttempts;
    }

    /// <summary>
    /// Add session engine services (CallSessionManager, extension points, hosted service).
    /// Auto-attaches to the single <see cref="VerbaraServer"/> on startup.
    /// Call after <see cref="AddVerbara(IServiceCollection, Action{VerbaraOptions})"/> for single-server deployments.
    /// </summary>
    public static IServiceCollection AddVerbaraSessions(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        return services;
    }

    /// <summary>
    /// Add session engine services and return an <see cref="ISessionsBuilder"/> so backend
    /// packages (Redis, Postgres, ...) can register themselves fluently:
    /// <c>services.AddVerbaraSessionsBuilder().UseRedis(...)</c>.
    /// Behaves identically to <see cref="AddVerbaraSessions"/>: registers the InMemory
    /// default store via <c>TryAddSingleton</c> and wires the auto-attach hosted services.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments using <see cref="VerbaraServerPool"/>.
    /// Does NOT register a hosted service — manual server attachment via
    /// <see cref="CallSessionManager.AttachToServer"/> / <see cref="CallSessionManager.DetachFromServer"/> is required.
    /// Call after <see cref="AddVerbaraMultiServer"/> for clustered deployments.
    /// </summary>
    public static IServiceCollection AddVerbaraSessionsMultiServer(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        return AddSessionsCore(services, configure);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments and return an
    /// <see cref="ISessionsBuilder"/> so backend packages can register themselves
    /// fluently. Behaves identically to <see cref="AddVerbaraSessionsMultiServer"/>
    /// otherwise.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsMultiServerBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        return new SessionsBuilder(services);
    }

    private static IServiceCollection AddSessionsCore(
        IServiceCollection services,
        Action<SessionOptions>? configure)
    {
        services.TryAddSingleton<ICallSessionManager, CallSessionManager>();
        services.TryAddSingleton<IAgentSessionTracker, AgentSessionTracker>();
        services.TryAddSingleton<IQueueSessionTracker, QueueSessionTracker>();
        services.TryAddSingleton<SessionStoreBase, InMemorySessionStore>();
        // Resolve ISessionStore through SessionStoreBase so custom overrides registered
        // by consumers (e.g. AddSingleton<SessionStoreBase, MyStore>()) continue to flow
        // through to anyone depending on the interface. TryAdd keeps this idempotent.
        services.TryAddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionStoreBase>());

        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();
        services.AddOptions<SessionOptions>().ValidateOnStart();
        services.AddHealthChecks()
            .AddCheck<Verbara.Sdk.Sessions.Diagnostics.SessionHealthCheck>("sessions");
        return services;
    }

    /// <summary>
    /// Register Verbara Sdk with multi-server support.
    /// Use <see cref="VerbaraServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    public static IServiceCollection AddVerbaraMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<IAriClientFactory, Verbara.Sdk.Ari.Client.AriClientFactory>();
        services.TryAddSingleton<VerbaraServerPool>();
        return services;
    }

    /// <summary>
    /// Register an ARI Outbound WebSocket listener. Asterisk 22.5+ with
    /// <c>application=outbound</c> in <c>ari.conf</c> will dial this listener
    /// instead of the consumer dialing Asterisk. A singleton
    /// <see cref="IAriOutboundListener"/> is registered along with a hosted
    /// service that starts/stops it with the application lifecycle.
    /// </summary>
    public static IServiceCollection AddAriOutboundListener(
        this IServiceCollection services,
        Action<AriOutboundListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AriOutboundListenerOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AriOutboundListenerOptions>, AriOutboundListenerOptionsValidator>();

        services.TryAddSingleton<IAriOutboundListener, AriOutboundListener>();
        services.AddSingleton<IHostedService, AriOutboundListenerHostedService>();
        return services;
    }
}

/// <summary>
/// Top-level configuration for all Verbara Sdk services.
/// </summary>
public sealed class VerbaraOptions
{
    public AmiConnectionOptions Ami { get; set; } = new();
    public AriClientOptions? Ari { get; set; }
    public int AgiPort { get; set; } = 4573;
    public IMappingStrategy? AgiMappingStrategy { get; set; }
}
