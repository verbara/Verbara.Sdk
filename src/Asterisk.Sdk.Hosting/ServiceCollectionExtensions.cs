using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Ami.Transport;
using Asterisk.Sdk.Ari.Audio;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Live.Server;
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

        // Live
        services.TryAddSingleton<AsteriskServer>();
        services.TryAddSingleton<IAsteriskServer>(sp => sp.GetRequiredService<AsteriskServer>());

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
            }

            // Use factory to safely inject optional IAudioServer
            services.TryAddSingleton<IAriClient>(sp => new AriClient(
                sp.GetRequiredService<IOptions<AriClientOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AriClient>>(),
                sp.GetService<IAudioServer>()));

            services.AddHealthChecks()
                .AddCheck<Asterisk.Sdk.Ari.Diagnostics.AriHealthCheck>("ari");
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
    /// Register Asterisk SDK with multi-server support.
    /// Use <see cref="AsteriskServerPool"/> to add and manage multiple Asterisk server connections.
    /// </summary>
    public static IServiceCollection AddAsteriskMultiServer(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
        services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
        services.TryAddSingleton<AsteriskServerPool>();
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
