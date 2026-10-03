using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.Cartesia;
using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using Verbara.Sdk.VoiceAi.Tts.DependencyInjection;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using Verbara.Sdk.VoiceAi.Tts.Lmnt;
using Verbara.Sdk.VoiceAi.Tts.Speechmatics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// A synthesizer's <c>ConnectTimeoutSeconds</c> accepts whole seconds from 1 to 600, and nothing else: the options
/// validator rejects any other value naming the option, the public constructor throws
/// <see cref="ArgumentOutOfRangeException"/> naming it (LMNT only on the WebSocket transport, the one that uses it),
/// and so does every synthesis started after the value was made unusable on the options object the client holds,
/// before it dials. LMNT's <c>HttpTimeoutSeconds</c> follows the same range on the HTTP transport. Before the range,
/// <c>0</c> failed every call as a handshake failure, <c>-1</c> threw from inside the connect's timer, and
/// Speechmatics and LMNT over HTTP threw from <see cref="HttpClient"/> naming no option.
/// </summary>
/// <remarks>
/// The ElevenLabs validator, which does not exist before the range, is pinned in
/// <c>ElevenLabs/ElevenLabsOptionsValidatorTests</c>; its registration is pinned here, through the options it resolves.
/// </remarks>
public sealed class ConnectTimeoutRangeTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string Option = "ConnectTimeoutSeconds";

    private const string HttpOption = "HttpTimeoutSeconds";

    private static readonly string[] WithAValidator = ["LMNT", "Speechmatics", "Cartesia", "Deepgram"];

    private static readonly string[] EveryClient = ["ElevenLabs", "LMNT", "Speechmatics", "Cartesia", "Deepgram"];

    public static TheoryData<string, int> UnusableWithAValidator => Cross(WithAValidator, [0, -1, 601]);

    public static TheoryData<string, int?> UsableWithAValidator => CrossUsable(WithAValidator);

    public static TheoryData<string, int> UnusableForEveryClient => Cross(EveryClient, [0, -1, 601]);

    public static TheoryData<string, int?> UsableForEveryClient => CrossUsable(EveryClient);

    public static TheoryData<string, int> MadeUnusable => Cross(["Cartesia", "Deepgram", "ElevenLabs", "LMNT"], [0, -1]);

    [Theory]
    [MemberData(nameof(UnusableWithAValidator))]
    public void Validate_ShouldFailNamingTheOption_WhenConnectTimeoutSecondsIsOutsideOneTo600(string client, int value)
    {
        var result = Validate(OptionsFor(client, value, port: 1));

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue($"{value} s is not a usable connect timeout");
            result.FailureMessage.Should().Contain(Option, "the failure names the option");
        }
    }

    [Theory]
    [MemberData(nameof(UsableWithAValidator))]
    public void Validate_ShouldSucceed_WhenConnectTimeoutSecondsIsInsideOneTo600(string client, int? value)
    {
        Validate(OptionsFor(client, value, port: 1)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(UnusableForEveryClient))]
    public async Task Ctor_ShouldThrowNamingTheOption_WhenConnectTimeoutSecondsIsOutsideOneTo600(string client, int value)
    {
        SpeechSynthesizer? created = null;
        var fault = Record.Exception(() => created = CreatePublic(OptionsFor(client, value, port: 1)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeOfType<ArgumentOutOfRangeException>($"{value} s is not a usable connect timeout")
            .Which.ParamName.Should().Be(Option);
    }

    [Theory]
    [MemberData(nameof(UsableForEveryClient))]
    public async Task Ctor_ShouldSucceed_WhenConnectTimeoutSecondsIsInsideOneTo600(string client, int? value)
    {
        SpeechSynthesizer? created = null;
        var fault = Record.Exception(() => created = CreatePublic(OptionsFor(client, value, port: 1)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(MadeUnusable))]
    public async Task SynthesizeAsync_ShouldThrowNamingTheOptionWithoutDialling_WhenConnectTimeoutSecondsWasMadeUnusableAfterConstruction(string client, int value)
    {
        // Arrange — a far end that would take the dial; the client is built with a usable value, which the host
        // then sets to an unusable one on the very object the client holds.
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var options = OptionsFor(client, null, stalled.Port);
        await using var synthesizer = client == "LMNT"
            ? new LmntSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create((LmntTtsOptions)options), stalled.Port)
            : CreatePublic(options);
        SetConnectTimeout(options, value);

        // Act
        var fault = await Record.ExceptionAsync(() => SynthesizeAsync(synthesizer).WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            fault.Should().BeOfType<ArgumentOutOfRangeException>(
                "a value made unusable after construction fails loudly, naming the option, not as a handshake failure")
                .Which.ParamName.Should().Be(Option);
            stalled.RequestCount.Should().Be(0, "nothing is dialled with an unusable bound");
        }
    }

    [Fact]
    public async Task Ctor_ShouldSucceed_WhenLmntIsOnHttpAndItsUnusedConnectTimeoutIsZero()
    {
        // Control: the HTTP transport never uses ConnectTimeoutSeconds, so its constructor does not check it.
        var options = new LmntTtsOptions { ApiKey = "test-key", Transport = LmntTransport.Http, ConnectTimeoutSeconds = 0 };
        LmntSpeechSynthesizer? created = null;
        var fault = Record.Exception(() => created = new LmntSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(options)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeNull();
    }

    [Fact]
    public void Validate_ShouldFailNamingTheOption_WhenLmntIsOnHttpAndItsUnusedConnectTimeoutIsZero()
    {
        // The validator's range has no transport: an HTTP host whose leftover value is outside 1–600 is rejected.
        var options = new LmntTtsOptions { ApiKey = "test-key", Transport = LmntTransport.Http, ConnectTimeoutSeconds = 0 };

        var result = new LmntTtsOptionsValidator().Validate(null, options);

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain(Option);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public void Validate_ShouldFailNamingTheOption_WhenLmntHttpTimeoutSecondsIsOutsideOneTo600(int value)
    {
        var options = new LmntTtsOptions { ApiKey = "test-key", HttpTimeoutSeconds = value };

        var result = new LmntTtsOptionsValidator().Validate(null, options);

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue($"{value} s is not a usable HTTP timeout");
            result.FailureMessage.Should().Contain(HttpOption, "the failure names the option");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(null)]
    public void Validate_ShouldSucceed_WhenLmntHttpTimeoutSecondsIsInsideOneTo600(int? value)
    {
        var options = new LmntTtsOptions { ApiKey = "test-key" };
        options.HttpTimeoutSeconds = value ?? options.HttpTimeoutSeconds;

        new LmntTtsOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public async Task Ctor_ShouldThrowNamingTheOption_WhenLmntIsOnHttpAndHttpTimeoutSecondsIsOutsideOneTo600(int value)
    {
        var options = new LmntTtsOptions { ApiKey = "test-key", Transport = LmntTransport.Http, HttpTimeoutSeconds = value };
        LmntSpeechSynthesizer? created = null;
        var fault = Record.Exception(() => created = new LmntSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(options)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeOfType<ArgumentOutOfRangeException>($"{value} s is not a usable HTTP timeout")
            .Which.ParamName.Should().Be(HttpOption);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public async Task Ctor_ShouldSucceed_WhenLmntIsOnWebSocketAndItsUnusedHttpTimeoutIsOutsideOneTo600(int value)
    {
        // Control: the WebSocket transport never uses HttpTimeoutSeconds, so its constructor does not check it.
        var options = new LmntTtsOptions { ApiKey = "test-key", Transport = LmntTransport.WebSocket, HttpTimeoutSeconds = value };
        LmntSpeechSynthesizer? created = null;
        var fault = Record.Exception(() => created = new LmntSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(options)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeNull();
    }

    [Fact]
    public async Task AddElevenLabsSpeechSynthesizer_ShouldRegisterAValidator_ThatRejectsAnUnusableConnectTimeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElevenLabsSpeechSynthesizer(o =>
        {
            o.ApiKey = "test";
            o.VoiceId = "test-voice";
            o.ConnectTimeoutSeconds = 0;
        });
        await using var provider = services.BuildServiceProvider();

        var fault = Record.Exception(() => provider.GetRequiredService<IOptions<ElevenLabsOptions>>().Value);

        fault.Should().BeOfType<OptionsValidationException>("the registration validates ElevenLabs's options as it does its siblings'")
            .Which.Message.Should().Contain(Option);
    }

    private static ValidateOptionsResult Validate(object options) => options switch
    {
        LmntTtsOptions o => new LmntTtsOptionsValidator().Validate(null, o),
        SpeechmaticsOptions o => new SpeechmaticsOptionsValidator().Validate(null, o),
        CartesiaOptions o => new CartesiaOptionsValidator().Validate(null, o),
        DeepgramTtsOptions o => new DeepgramTtsOptionsValidator().Validate(null, o),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    /// <summary>The options of <paramref name="client"/>, usable but for <paramref name="value"/> (null: the default).</summary>
    private static object OptionsFor(string client, int? value, int port) => client switch
    {
        "ElevenLabs" => new ElevenLabsOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{port}/v1/text-to-speech",
            ConnectTimeoutSeconds = value ?? new ElevenLabsOptions().ConnectTimeoutSeconds,
        },
        "LMNT" => new LmntTtsOptions
        {
            ApiKey = "test-key",
            Voice = LmntVoices.Leah,
            Transport = LmntTransport.WebSocket,
            ConnectTimeoutSeconds = value ?? new LmntTtsOptions().ConnectTimeoutSeconds,
        },
        "Speechmatics" => new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            ConnectTimeoutSeconds = value ?? new SpeechmaticsOptions().ConnectTimeoutSeconds,
        },
        "Cartesia" => new CartesiaOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice",
            BaseUri = $"ws://127.0.0.1:{port}/tts/websocket",
            ConnectTimeoutSeconds = value ?? new CartesiaOptions().ConnectTimeoutSeconds,
        },
        "Deepgram" => new DeepgramTtsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v1/speak",
            ConnectTimeoutSeconds = value ?? new DeepgramTtsOptions().ConnectTimeoutSeconds,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    /// <summary>The client, through its public constructor.</summary>
    private static SpeechSynthesizer CreatePublic(object options) => options switch
    {
        ElevenLabsOptions o => new ElevenLabsSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(o)),
        LmntTtsOptions o => new LmntSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(o)),
        SpeechmaticsOptions o => new SpeechmaticsSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(o)),
        CartesiaOptions o => new CartesiaSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(o)),
        DeepgramTtsOptions o => new DeepgramSpeechSynthesizer(Microsoft.Extensions.Options.Options.Create(o)),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    private static void SetConnectTimeout(object options, int value)
    {
        switch (options)
        {
            case ElevenLabsOptions o: o.ConnectTimeoutSeconds = value; break;
            case LmntTtsOptions o: o.ConnectTimeoutSeconds = value; break;
            case CartesiaOptions o: o.ConnectTimeoutSeconds = value; break;
            case DeepgramTtsOptions o: o.ConnectTimeoutSeconds = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static TheoryData<string, int> Cross(string[] clients, int[] values)
    {
        var data = new TheoryData<string, int>();
        foreach (var client in clients)
        {
            foreach (var value in values)
                data.Add(client, value);
        }

        return data;
    }

    private static TheoryData<string, int?> CrossUsable(string[] clients)
    {
        var data = new TheoryData<string, int?>();
        foreach (var client in clients)
        {
            data.Add(client, 1);
            data.Add(client, 600);
            data.Add(client, null);
        }

        return data;
    }

    /// <summary>One synthesis to its end, on its own task.</summary>
    private static Task SynthesizeAsync(SpeechSynthesizer synthesizer)
        => Task.Run(async () =>
        {
            await foreach (var _ in synthesizer.SynthesizeAsync("hello world", AudioFormat.Slin16Mono8kHz, CancellationToken.None))
            {
                // Nothing is expected: the synthesis must fail before it dials.
            }
        });
}
