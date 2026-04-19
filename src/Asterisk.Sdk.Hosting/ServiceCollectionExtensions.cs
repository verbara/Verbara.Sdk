using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.Ari.Audio;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Outbound;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Extension methods for registering Asterisk SDK services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Asterisk SDK services (AMI, AGI, ARI, Live) to the service collection.
    /// Configures a single Asterisk server connection with options validation on startup.
    /// </summary>
    public static IServiceCollection AddAsterisk(
        this IServiceCollection services,
        Action<AsteriskOptions> configure)
    {
        var options = new AsteriskOptions();
        configure(options);

        // Transport
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();

        // AMI (single-server) with AOT-safe source-generated validation
        services.AddOptions<AmiConnectionOptions>()
            .Configure(o =>
            {
                o.Hostname = options.Ami.Hostname;
                o.Port = options.Ami.Port;
                o.Username = options.Ami.Username;
                o.Password = options.Ami.Password;
                o.UseSsl = options.Ami.UseSsl;
                o.AutoReconnect = options.Ami.AutoReconnect;
            })
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
            .AddCheck<Asterisk.Sdk.Agi.Diagnostics.AgiHealthCheck>("agi");
        services.AddSingleton<IHostedService, Asterisk.Sdk.Agi.Hosting.AgiHostedService>();

        // Live
        services.TryAddSingleton<AsteriskServer>();
        services.TryAddSingleton<IAsteriskServer>(sp => sp.GetRequiredService<AsteriskServer>());
        services.AddHealthChecks()
            .AddCheck<Asterisk.Sdk.Live.Diagnostics.LiveHealthCheck>("live");

        // Hosted services for automatic lifecycle management
        services.AddSingleton<IHostedService, AmiConnectionHostedService>();
        services.AddSingleton<IHostedService, AsteriskServerHostedService>();

        // Health checks
        services.AddHealthChecks()
            .AddCheck<Asterisk.Sdk.Ami.Diagnostics.AmiHealthCheck>("ami");

        // ARI with validation
        if (options.Ari is not null)
        {
            services.AddOptions<AriClientOptions>()
                .Configure(o =>
                {
                    o.BaseUrl = options.Ari.BaseUrl;
                    o.Username = options.Ari.Username;
                    o.Password = options.Ari.Password;
                    o.Application = options.Ari.Application;
                })
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
                .AddCheck<Asterisk.Sdk.Ari.Diagnostics.AriHealthCheck>("ari");
            services.AddSingleton<IHostedService, AriConnectionHostedService>();
        }

        return services;
    }

    /// <summary>
    /// Add all Asterisk SDK services binding options from <see cref="IConfiguration"/>.
    /// Expects an "Asterisk" section with "Ami", "Ari", etc. sub-sections.
    /// AOT-safe: manually reads configuration keys instead of using reflection-based Bind().
    /// </summary>
    public static IServiceCollection AddAsterisk(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var ami = configuration.GetSection("Asterisk:Ami");
        var ari = configuration.GetSection("Asterisk:Ari");

        return services.AddAsterisk(o =>
        {
            if (ami[nameof(o.Ami.Hostname)] is { } hostname) o.Ami.Hostname = hostname;
            if (int.TryParse(ami[nameof(o.Ami.Port)], out var port)) o.Ami.Port = port;
            if (ami[nameof(o.Ami.Username)] is { } username) o.Ami.Username = username;
            if (ami[nameof(o.Ami.Password)] is { } password) o.Ami.Password = password;
            if (bool.TryParse(ami[nameof(o.Ami.UseSsl)], out var useSsl)) o.Ami.UseSsl = useSsl;
            if (bool.TryParse(ami[nameof(o.Ami.AutoReconnect)], out var autoReconnect)) o.Ami.AutoReconnect = autoReconnect;

            if (int.TryParse(configuration["Asterisk:AgiPort"], out var agiPort)) o.AgiPort = agiPort;

            if (ari.Exists())
            {
                o.Ari = new AriClientOptions();
                if (ari[nameof(o.Ari.BaseUrl)] is { } baseUrl) o.Ari.BaseUrl = baseUrl;
                if (ari[nameof(o.Ari.Username)] is { } ariUser) o.Ari.Username = ariUser;
                if (ari[nameof(o.Ari.Password)] is { } ariPass) o.Ari.Password = ariPass;
                if (ari[nameof(o.Ari.Application)] is { } app) o.Ari.Application = app;
            }
        });
    }

    /// <summary>
    /// Add session engine services (CallSessionManager, extension points, hosted service).
    /// Auto-attaches to the single <see cref="AsteriskServer"/> on startup.
    /// Call after <see cref="AddAsterisk(IServiceCollection, Action{AsteriskOptions})"/> for single-server deployments.
    /// </summary>
    public static IServiceCollection AddAsteriskSessions(
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
    /// <c>services.AddAsteriskSessionsBuilder().UseRedis(...)</c>.
    /// Behaves identically to <see cref="AddAsteriskSessions"/>: registers the InMemory
    /// default store via <c>TryAddSingleton</c> and wires the auto-attach hosted services.
    /// </summary>
    public static ISessionsBuilder AddAsteriskSessionsBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments using <see cref="AsteriskServerPool"/>.
    /// Does NOT register a hosted service — manual server attachment via
    /// <see cref="CallSessionManager.AttachToServer"/> / <see cref="CallSessionManager.DetachFromServer"/> is required.
    /// Call after <see cref="AddAsteriskMultiServer"/> for clustered deployments.
    /// </summary>
    public static IServiceCollection AddAsteriskSessionsMultiServer(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        return AddSessionsCore(services, configure);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments and return an
    /// <see cref="ISessionsBuilder"/> so backend packages can register themselves
    /// fluently. Behaves identically to <see cref="AddAsteriskSessionsMultiServer"/>
    /// otherwise.
    /// </summary>
    public static ISessionsBuilder AddAsteriskSessionsMultiServerBuilder(
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
            .AddCheck<Asterisk.Sdk.Sessions.Diagnostics.SessionHealthCheck>("sessions");
        return services;
    }

    /// <summary>
    /// Register Asterisk SDK with multi-server support.
    /// Use <see cref="AsteriskServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    public static IServiceCollection AddAsteriskMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<IAriClientFactory, Asterisk.Sdk.Ari.Client.AriClientFactory>();
        services.TryAddSingleton<AsteriskServerPool>();
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
/// Top-level configuration for all Asterisk SDK services.
/// </summary>
public sealed class AsteriskOptions
{
    public AmiConnectionOptions Ami { get; set; } = new();
    public AriClientOptions? Ari { get; set; }
    public int AgiPort { get; set; } = 4573;
    public IMappingStrategy? AgiMappingStrategy { get; set; }
}
