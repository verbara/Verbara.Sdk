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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    /// Auto-attaches to the single <see cref="VerbaraServer"/> on startup, before the server's first load, so calls
    /// already in progress at start have a session.
    /// Call after <see cref="AddVerbara(IServiceCollection, Action{VerbaraOptions})"/> for single-server deployments.
    /// </summary>
    public static IServiceCollection AddVerbaraSessions(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        services.TryAddSingleton(SingleServerSweepRegistration.Instance);
        return services;
    }

    /// <summary>
    /// Add session engine services and return an <see cref="ISessionsBuilder"/> so backend
    /// packages (Redis, Postgres, ...) can register themselves fluently:
    /// <c>services.AddVerbaraSessionsBuilder().UseRedis(...)</c>.
    /// Behaves identically to <see cref="AddVerbaraSessions"/>: registers the InMemory
    /// default store via <c>TryAddSingleton</c> and wires the auto-attach hosted services, which attach
    /// before the server's first load, so calls already in progress at start have a session.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        services.AddSingleton<IHostedService, SessionManagerHostedService>();
        services.AddSingleton<IHostedService, SessionReconciliationService>();
        services.TryAddSingleton(SingleServerSweepRegistration.Instance);
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Add session engine services for multi-server deployments using <see cref="VerbaraServerPool"/>, and the
    /// reconciliation sweep of the pool. Call after <see cref="AddVerbaraMultiServer"/> for clustered deployments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servers are not attached for you: attach each one with <see cref="CallSessionManager.AttachToServer"/> under
    /// the id it has in the pool, and detach it with <see cref="CallSessionManager.DetachFromServer"/>.
    /// </para>
    /// <para>
    /// The sweep is a hosted service. On each <see cref="SessionOptions.ReconciliationInterval"/> tick (default 30
    /// seconds) it walks the servers the pool holds and, for each one, verifies the held calls attached under that
    /// server's id that are older than <see cref="SessionOptions.DialingTimeout"/> against one <c>Status</c> of that
    /// server, sent only when there is such a call. A call whose channels the completed snapshot omits ends as a reload
    /// ends it (<c>cause=reload</c>, no hangup cause); a call Asterisk still lists is left alone. A server whose
    /// verification fails is logged and skipped for that tick; the other servers are verified. A held call whose
    /// server is not in the pool is left alone. <see cref="Timeout.InfiniteTimeSpan"/> switches the sweep off
    /// (<c>configure: o =&gt; o.ReconciliationInterval = Timeout.InfiniteTimeSpan</c>); any other interval of zero or
    /// less fails the host's start with <see cref="ArgumentOutOfRangeException"/>. Calling this method more than once
    /// registers one sweep; on a host that also calls <see cref="AddVerbaraSessions"/>, the single DI server is left to
    /// that registration's sweep.
    /// </para>
    /// <para>
    /// The sessions are found by the channel ids Asterisk issues, so the servers of a pool must not issue the same ids:
    /// give every Asterisk of the pool its own <c>systemname</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddVerbaraSessionsMultiServer(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        AddPoolSweep(services);
        return services;
    }

    /// <summary>
    /// Add session engine services for multi-server deployments, and the reconciliation sweep of the pool, and return an
    /// <see cref="ISessionsBuilder"/> so backend packages can register themselves fluently. Behaves identically to
    /// <see cref="AddVerbaraSessionsMultiServer"/> otherwise, sweep included.
    /// </summary>
    public static ISessionsBuilder AddVerbaraSessionsMultiServerBuilder(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        AddSessionsCore(services, configure);
        AddPoolSweep(services);
        return new SessionsBuilder(services);
    }

    /// <summary>
    /// Registers the pool's reconciliation sweep once, however many multi-server registrations call it, through an
    /// explicit factory (no constructor reflection).
    /// </summary>
    private static void AddPoolSweep(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PoolReconciliationService>(sp =>
            new PoolReconciliationService(
                sp.GetRequiredService<ICallSessionManager>(),
                sp.GetService<VerbaraServerPool>(),
                sp.GetService<SingleServerSweepRegistration>() is null ? null : sp.GetService<VerbaraServer>(),
                sp.GetRequiredService<IOptions<SessionOptions>>(),
                sp.GetRequiredService<ILogger<PoolReconciliationService>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System)));

    /// <summary>
    /// Registered by the single-server sessions registrations: tells the pool sweep that the single DI server is
    /// verified by the single-server sweep.
    /// </summary>
    internal sealed class SingleServerSweepRegistration
    {
        public static readonly SingleServerSweepRegistration Instance = new();

        private SingleServerSweepRegistration()
        {
        }
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
        // Guarded, not a plain AddCheck: a host may call more than one sessions registration (the single-server and the
        // multi-server one, or one of them twice), and a second registration under the same name makes
        // HealthCheckService throw "Duplicate health checks were registered" on resolve.
        services.AddHealthChecks();
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations)
            {
                if (string.Equals(registration.Name, SessionsHealthCheckName, StringComparison.Ordinal))
                    return;
            }

            options.Registrations.Add(new HealthCheckRegistration(
                SessionsHealthCheckName,
                sp => new Verbara.Sdk.Sessions.Diagnostics.SessionHealthCheck(sp.GetRequiredService<ICallSessionManager>()),
                failureStatus: null,
                tags: null));
        });
        return services;
    }

    /// <summary>
    /// Register Verbara Sdk with multi-server support.
    /// Use <see cref="VerbaraServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    /// <remarks>
    /// Also registers one <see cref="Verbara.Sdk.Live.Diagnostics.VerbaraServerPoolHealthCheck"/> under the name
    /// <c>verbara-pool</c>, with no tags: it reads the AMI connection of every server the pool holds when it runs.
    /// Calling this method more than once registers the check once.
    /// </remarks>
    public static IServiceCollection AddVerbaraMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<IAriClientFactory, Verbara.Sdk.Ari.Client.AriClientFactory>();
        services.TryAddSingleton<VerbaraServerPool>();

        // Guarded, not a plain AddCheck: every call to this method adds this Configure, and a second registration
        // under the same name makes HealthCheckService throw "Duplicate health checks were registered" on resolve.
        services.AddHealthChecks();
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations)
            {
                if (string.Equals(registration.Name, ServerPoolHealthCheckName, StringComparison.Ordinal))
                    return;
            }

            options.Registrations.Add(new HealthCheckRegistration(
                ServerPoolHealthCheckName,
                sp => new Verbara.Sdk.Live.Diagnostics.VerbaraServerPoolHealthCheck(sp.GetRequiredService<VerbaraServerPool>()),
                failureStatus: null,
                tags: null));
        });
        return services;
    }

    /// <summary>The name every sessions registration registers the session engine's health check under, once.</summary>
    private const string SessionsHealthCheckName = "sessions";

    /// <summary>The name <see cref="AddVerbaraMultiServer"/> registers the pool health check under.</summary>
    private const string ServerPoolHealthCheckName = "verbara-pool";

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
