using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// That an option a host sets through <c>AddVerbara</c> — inline, or from the <c>Asterisk:Ami</c> / <c>Asterisk:Ari</c>
/// configuration sections — is the option the AMI connection or the ARI client runs with, for every settable option of
/// both types, and that every configuration key the shipped READMEs and guides show is one <c>AddVerbara</c> reads.
/// Shape ruled by the owner (H91, 2026-09-30): the sections are bound whole, and the inline path copies every option.
/// Nothing here starts a host (see <see cref="ServiceCollectionExtensionsTests"/>): options are read from the provider,
/// and start-time validation is driven through <see cref="IStartupValidator"/>.
/// </summary>
public sealed partial class AddVerbaraOptionsDeliveryTests
{
    /// <summary>Every settable AMI option, with a value that is not its default, as configuration text and as set inline.</summary>
    private static readonly Dictionary<string, (string Text, Action<AmiConnectionOptions> Set, Func<AmiConnectionOptions, object> Get)> AmiOptions = new()
    {
        [nameof(AmiConnectionOptions.Hostname)] = ("pbx.h91.example", o => o.Hostname = "pbx.h91.example", o => o.Hostname),
        [nameof(AmiConnectionOptions.Port)] = ("6038", o => o.Port = 6038, o => o.Port),
        [nameof(AmiConnectionOptions.Username)] = ("h91-user", o => o.Username = "h91-user", o => o.Username),
        [nameof(AmiConnectionOptions.Password)] = ("h91-secret", o => o.Password = "h91-secret", o => o.Password),
        [nameof(AmiConnectionOptions.UseSsl)] = ("true", o => o.UseSsl = true, o => o.UseSsl),
        [nameof(AmiConnectionOptions.ConnectionTimeout)] = ("00:00:17", o => o.ConnectionTimeout = TimeSpan.FromSeconds(17), o => o.ConnectionTimeout),
        [nameof(AmiConnectionOptions.ReadTimeout)] = ("00:00:19", o => o.ReadTimeout = TimeSpan.FromSeconds(19), o => o.ReadTimeout),
        [nameof(AmiConnectionOptions.DefaultResponseTimeout)] = ("00:00:07", o => o.DefaultResponseTimeout = TimeSpan.FromSeconds(7), o => o.DefaultResponseTimeout),
        [nameof(AmiConnectionOptions.DefaultEventTimeout)] = ("00:00:09", o => o.DefaultEventTimeout = TimeSpan.FromSeconds(9), o => o.DefaultEventTimeout),
        [nameof(AmiConnectionOptions.AutoReconnect)] = ("false", o => o.AutoReconnect = false, o => o.AutoReconnect),
        [nameof(AmiConnectionOptions.MaxReconnectAttempts)] = ("3", o => o.MaxReconnectAttempts = 3, o => o.MaxReconnectAttempts),
        [nameof(AmiConnectionOptions.EventPumpCapacity)] = ("12345", o => o.EventPumpCapacity = 12345, o => o.EventPumpCapacity),
        [nameof(AmiConnectionOptions.ReconnectInitialDelay)] = ("00:00:03", o => o.ReconnectInitialDelay = TimeSpan.FromSeconds(3), o => o.ReconnectInitialDelay),
        [nameof(AmiConnectionOptions.ReconnectMaxDelay)] = ("00:00:45", o => o.ReconnectMaxDelay = TimeSpan.FromSeconds(45), o => o.ReconnectMaxDelay),
        [nameof(AmiConnectionOptions.ReconnectMultiplier)] = ("1.5", o => o.ReconnectMultiplier = 1.5, o => o.ReconnectMultiplier),
        [nameof(AmiConnectionOptions.EnableHeartbeat)] = ("false", o => o.EnableHeartbeat = false, o => o.EnableHeartbeat),
        [nameof(AmiConnectionOptions.HeartbeatInterval)] = ("00:00:11", o => o.HeartbeatInterval = TimeSpan.FromSeconds(11), o => o.HeartbeatInterval),
        [nameof(AmiConnectionOptions.HeartbeatTimeout)] = ("00:00:04", o => o.HeartbeatTimeout = TimeSpan.FromSeconds(4), o => o.HeartbeatTimeout),
    };

    /// <summary>Every settable ARI option that configuration can carry (the audio-server callback is a delegate; see its own test).</summary>
    private static readonly Dictionary<string, (string Text, Action<AriClientOptions> Set, Func<AriClientOptions, object> Get)> AriOptions = new()
    {
        [nameof(AriClientOptions.BaseUrl)] = ("http://pbx.h91.example:9088", o => o.BaseUrl = "http://pbx.h91.example:9088", o => o.BaseUrl),
        [nameof(AriClientOptions.Username)] = ("h91-ari-user", o => o.Username = "h91-ari-user", o => o.Username),
        [nameof(AriClientOptions.Password)] = ("h91-ari-secret", o => o.Password = "h91-ari-secret", o => o.Password),
        [nameof(AriClientOptions.Application)] = ("h91-app", o => o.Application = "h91-app", o => o.Application),
        [nameof(AriClientOptions.AutoReconnect)] = ("false", o => o.AutoReconnect = false, o => o.AutoReconnect),
        [nameof(AriClientOptions.ReconnectInitialDelay)] = ("00:00:03", o => o.ReconnectInitialDelay = TimeSpan.FromSeconds(3), o => o.ReconnectInitialDelay),
        [nameof(AriClientOptions.ReconnectMaxDelay)] = ("00:00:45", o => o.ReconnectMaxDelay = TimeSpan.FromSeconds(45), o => o.ReconnectMaxDelay),
        [nameof(AriClientOptions.ReconnectMultiplier)] = ("1.5", o => o.ReconnectMultiplier = 1.5, o => o.ReconnectMultiplier),
        [nameof(AriClientOptions.MaxReconnectAttempts)] = ("3", o => o.MaxReconnectAttempts = 3, o => o.MaxReconnectAttempts),
    };

    /// <summary>
    /// The two lists above are the set of options these tests prove are delivered, and <c>CopyAmiOptions</c> /
    /// <c>CopyAriOptions</c> are hand-written: an option added to either type later, and to neither list, would be
    /// dropped by <c>AddVerbara(o => …)</c> with every test here still green. Read by reflection, in the test only.
    /// </summary>
    [Fact]
    public void OptionLists_ShouldNameEverySettableOption_OfBothOptionTypes()
    {
        static string[] Settable([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
            [.. type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal)];

        using (new AssertionScope())
        {
            AmiOptions.Keys.Order(StringComparer.Ordinal).Should().Equal(Settable(typeof(AmiConnectionOptions)),
                "every settable AmiConnectionOptions option is one whose delivery through AddVerbara is tested");
            AriOptions.Keys.Append(nameof(AriClientOptions.ConfigureAudioServer)).Order(StringComparer.Ordinal)
                .Should().Equal(Settable(typeof(AriClientOptions)),
                    "every settable AriClientOptions option is one whose delivery through AddVerbara is tested (the audio-server callback by its own test)");
        }
    }

    public static TheoryData<string> AmiOptionNames => new(AmiOptions.Keys);

    public static TheoryData<string> AriOptionNames => new(AriOptions.Keys);

    [Theory]
    [MemberData(nameof(AmiOptionNames))]
    public async Task AddVerbara_ShouldDeliverEveryAmiOption_WhenSetInline(string option)
    {
        var (_, set, get) = AmiOptions[option];
        var expected = get(Changed(set));

        await using var provider = Build(services => services.AddVerbara(o => set(o.Ami)));

        get(provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value).Should().Be(expected,
            $"AmiConnectionOptions.{option} set on AddVerbara(o => o.Ami.{option} = …) is the value the connection runs with");
    }

    [Theory]
    [MemberData(nameof(AriOptionNames))]
    public async Task AddVerbara_ShouldDeliverEveryAriOption_WhenSetInline(string option)
    {
        var (_, set, get) = AriOptions[option];
        var expected = get(Changed(set));

        await using var provider = Build(services => services.AddVerbara(o =>
        {
            o.Ari = new AriClientOptions();
            set(o.Ari);
        }));

        get(provider.GetRequiredService<IOptions<AriClientOptions>>().Value).Should().Be(expected,
            $"AriClientOptions.{option} set on AddVerbara(o => o.Ari.{option} = …) is the value the client runs with");
    }

    [Fact]
    public async Task AddVerbara_ShouldDeliverTheAudioServerCallback_WhenSetInline()
    {
        Action<AudioServerOptions> configureAudioServer = a => a.AudioSocketPort = 9191;

        await using var provider = Build(services => services.AddVerbara(o =>
            o.Ari = new AriClientOptions { ConfigureAudioServer = configureAudioServer }));

        using (new AssertionScope())
        {
            provider.GetRequiredService<IOptions<AriClientOptions>>().Value.ConfigureAudioServer.Should().BeSameAs(configureAudioServer,
                "every option set inline reaches the resolved ARI options, the audio-server callback included");
            provider.GetRequiredService<AudioServerOptions>().AudioSocketPort.Should().Be(9191,
                "the callback still configures the registered audio servers, as it does today");
        }
    }

    [Theory]
    [MemberData(nameof(AmiOptionNames))]
    public async Task AddVerbara_ShouldDeliverEveryAmiOption_WhenReadFromConfiguration(string option)
    {
        var (text, set, get) = AmiOptions[option];
        var expected = get(Changed(set));
        var configuration = Configuration(new() { [$"Asterisk:Ami:{option}"] = text });

        await using var provider = Build(services => services.AddVerbara(configuration));

        get(provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value).Should().Be(expected,
            $"Asterisk:Ami:{option} = \"{text}\" is read by AddVerbara(configuration)");
    }

    [Theory]
    [MemberData(nameof(AriOptionNames))]
    public async Task AddVerbara_ShouldDeliverEveryAriOption_WhenReadFromConfiguration(string option)
    {
        var (text, set, get) = AriOptions[option];
        var expected = get(Changed(set));
        var configuration = Configuration(new() { [$"Asterisk:Ari:{option}"] = text });

        await using var provider = Build(services => services.AddVerbara(configuration));

        get(provider.GetRequiredService<IOptions<AriClientOptions>>().Value).Should().Be(expected,
            $"Asterisk:Ari:{option} = \"{text}\" is read by AddVerbara(configuration)");
    }

    [Fact]
    public async Task AddVerbara_ShouldReadAMultiplierInvariantly_WhenTheCurrentCultureUsesADecimalComma()
    {
        var configuration = Configuration(new() { ["Asterisk:Ami:ReconnectMultiplier"] = "1.5" });
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            await using var provider = Build(services => services.AddVerbara(configuration));

            provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value.ReconnectMultiplier.Should().Be(1.5,
                "a configuration value is parsed with the invariant culture: under de-DE \"1.5\" must not become 15 or be ignored");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Asterisk:Ami:Port", "abc")]
    [InlineData("Asterisk:Ami:AutoReconnect", "yes")]
    [InlineData("Asterisk:Ami:MaxReconnectAttempts", "three")]
    [InlineData("Asterisk:Ami:ReconnectInitialDelay", "soon")]
    [InlineData("Asterisk:Ari:MaxReconnectAttempts", "three")]
    public void AddVerbara_ShouldFailNamingTheKey_WhenAConfiguredValueIsMalformed(string key, string text)
    {
        var configuration = Configuration(new() { [key] = text });

        var act = () =>
        {
            using var provider = Build(services => services.AddVerbara(configuration));
            _ = provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<AriClientOptions>>().Value;
        };

        act.Should().Throw<InvalidOperationException>(
                $"{key} = \"{text}\" cannot be converted, and a malformed value fails loudly instead of leaving the default in place")
            .Which.Message.Should().Contain(key.Split(':')[^1], "the failure names the key to fix");
    }

    [Theory]
    [InlineData("Ami")]
    [InlineData("Ari")]
    public async Task AddVerbara_ShouldFailStartupValidation_WhenAnUnusableMultiplierIsSetInline(string client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(o =>
        {
            o.Ami.Username = "admin";
            o.Ami.Password = "secret";
            if (client == "Ami")
                o.Ami.ReconnectMultiplier = 0.5;
            else
                o.Ari = new AriClientOptions { Username = "admin", Password = "secret", Application = "app", ReconnectMultiplier = 0.5 };
        });
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IStartupValidator>();

        var act = () => validator.Validate();

        act.Should().Throw<OptionsValidationException>(
                $"a {client} multiplier of 0.5 set inline on AddVerbara reaches the options, and with AutoReconnect on the host must not start with it")
            .Which.Message.Should().Contain(nameof(AmiConnectionOptions.ReconnectMultiplier), "the failure names the option to fix");
    }

    [Fact]
    public async Task AddVerbara_ShouldLetAnInlineValueWin_OverAConfigurePlacedBeforeIt()
    {
        await using var provider = Build(services =>
        {
            services.Configure<AmiConnectionOptions>(o => o.MaxReconnectAttempts = 7);
            services.AddVerbara(o => o.Ami.MaxReconnectAttempts = 5);
        });

        provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value.MaxReconnectAttempts.Should().Be(5,
            "AddVerbara applies every option it is given, so its value replaces one configured before it (H91, shape C + A)");
    }

    [Fact]
    public async Task AddVerbara_ShouldLetAConfigurePlacedAfterItWin_ForACopiedAndAReconnectOption()
    {
        await using var provider = Build(services =>
        {
            services.AddVerbara(o =>
            {
                o.Ami.Hostname = "inline";
                o.Ami.MaxReconnectAttempts = 5;
            });
            services.Configure<AmiConnectionOptions>(o =>
            {
                o.Hostname = "configured";
                o.MaxReconnectAttempts = 7;
            });
        });

        var options = provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value;
        using (new AssertionScope())
        {
            options.Hostname.Should().Be("configured", "a Configure<T> after AddVerbara adjusts what AddVerbara set");
            options.MaxReconnectAttempts.Should().Be(7, "a Configure<T> after AddVerbara adjusts what AddVerbara set");
        }
    }

    /// <summary>
    /// Every JSON block of the shipped READMEs and guides that configures AMI, ARI or AGI, as (file, line of its fence).
    /// Read from the tree at discovery, so a block added later is covered without editing this list.
    /// </summary>
    public static TheoryData<string, int> DocumentedConfigurationBlocks
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var (file, line, _) in DocumentedBlocks())
                data.Add(file, line);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DocumentedConfigurationBlocks))]
    public async Task AddVerbara_ShouldDeliverEveryKey_OfADocumentedConfigurationBlock(string file, int line)
    {
        var json = DocumentedBlocks().Single(b => b.File == file && b.Line == line).Json;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var leaves = configuration.AsEnumerable().Where(kv => kv.Value is not null).ToList();

        await using var provider = Build(services => services.AddVerbara(configuration));
        var ami = provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value;
        var ari = provider.GetRequiredService<IOptions<AriClientOptions>>().Value;
        var agiPort = ((FastAgiServer)provider.GetRequiredService<IAgiServer>()).Port;

        using (new AssertionScope())
        {
            leaves.Should().NotBeEmpty($"{file}:{line} is a configuration block");
            foreach (var (key, text) in leaves)
            {
                var delivered = Delivered(key, ami, ari, agiPort);
                delivered.Should().NotBeNull(
                    $"{file}:{line} shows the key {key}, which must be one AddVerbara(configuration) reads " +
                    "(Asterisk:Ami:<option>, Asterisk:Ari:<option> or Asterisk:AgiPort)");
                if (delivered is not null)
                    delivered.Should().Be(Parse(text!, delivered.GetType()),
                        $"{file}:{line} sets {key} = \"{text}\", and that is the value the client runs with");
            }
        }
    }

    [Fact]
    public async Task AddVerbara_ShouldPassStartupValidation_WhenConfiguredWithTheHostingReadmeExample()
    {
        var (_, _, json) = DocumentedBlocks().First(b => b.File == "src/Verbara.Sdk.Hosting/README.md");
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbara(configuration);
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IStartupValidator>();

        var act = () => validator.Validate();

        act.Should().NotThrow("the Hosting README's quick start, applied as written, is a host that starts");
    }

    private static object? Delivered(string key, AmiConnectionOptions ami, AriClientOptions ari, int agiPort)
    {
        var parts = key.Split(':');
        if (parts.Length == 2 && parts[0] == "Asterisk" && parts[1] == "AgiPort")
            return agiPort;
        if (parts.Length != 3 || parts[0] != "Asterisk")
            return null;
        return parts[1] switch
        {
            "Ami" when AmiOptions.TryGetValue(parts[2], out var o) => o.Get(ami),
            "Ari" when AriOptions.TryGetValue(parts[2], out var o) => o.Get(ari),
            _ => null,
        };
    }

    private static object Parse(string text, Type type) => type switch
    {
        _ when type == typeof(string) => text,
        _ when type == typeof(int) => int.Parse(text, CultureInfo.InvariantCulture),
        _ when type == typeof(double) => double.Parse(text, CultureInfo.InvariantCulture),
        _ when type == typeof(bool) => bool.Parse(text),
        _ when type == typeof(TimeSpan) => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an option type of these tests."),
    };

    private static IEnumerable<(string File, int Line, string Json)> DocumentedBlocks()
    {
        var root = RepoRoot();
        var files = new[] { Path.Join(root, "README.md") }
            .Concat(Directory.GetDirectories(Path.Join(root, "src")).Select(d => Path.Join(d, "README.md")))
            .Concat(Directory.GetFiles(Path.Join(root, "docs", "guides"), "*.md"))
            .Concat(Directory.GetDirectories(Path.Join(root, "Examples")).Select(d => Path.Join(d, "README.md")))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!JsonFence().IsMatch(lines[i]))
                    continue;
                var end = i + 1;
                while (end < lines.Length && !ClosingFence().IsMatch(lines[end]))
                    end++;
                var body = string.Join('\n', lines[(i + 1)..end]);
                if (ConfiguresAsterisk().IsMatch(body))
                    yield return (Path.GetRelativePath(root, path).Replace('\\', '/'), i + 1, body);
                i = end;
            }
        }
    }

    [GeneratedRegex(@"^\s*```jsonc?\s*$")]
    private static partial Regex JsonFence();

    [GeneratedRegex(@"^\s*```\s*$")]
    private static partial Regex ClosingFence();

    [GeneratedRegex("\"(Ami|Ari|Agi|AgiPort|AmiConnection|AriClient|Asterisk)\"\\s*:")]
    private static partial Regex ConfiguresAsterisk();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Sdk.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (Verbara.Sdk.slnx).");
    }

    private static T Changed<T>(Action<T> set) where T : new()
    {
        var options = new T();
        set(options);
        return options;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// A provider with the options validators removed, so that a value under test is read even when the example leaves a
    /// required field (a user name) out; validation itself is asserted through <see cref="IStartupValidator"/> elsewhere.
    /// </summary>
    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        register(services);
        services.RemoveAll<IValidateOptions<AmiConnectionOptions>>();
        services.RemoveAll<IValidateOptions<AriClientOptions>>();
        return services.BuildServiceProvider();
    }
}
