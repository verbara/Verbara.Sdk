using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Stt.AssemblyAi;
using Verbara.Sdk.VoiceAi.Stt.Cartesia;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.DependencyInjection;
using Verbara.Sdk.VoiceAi.Stt.Speechmatics;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// A recognizer's <c>ConnectTimeoutSeconds</c> accepts whole seconds from 1 to 600, and nothing else: the options
/// validator rejects any other value naming the option, the public constructor throws
/// <see cref="ArgumentOutOfRangeException"/> naming it, and so does every stream started after the value was made
/// unusable on the options object the client holds, before it dials. Before the range, <c>0</c> failed every call
/// as a handshake failure and <c>-1</c> threw from inside the connect's timer.
/// </summary>
/// <remarks>
/// The Deepgram validator, which does not exist before the range, is pinned in
/// <c>Deepgram/DeepgramOptionsValidatorTests</c>; its registration is pinned here, through the options it resolves.
/// </remarks>
public sealed class ConnectTimeoutRangeTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string Option = "ConnectTimeoutSeconds";

    public static TheoryData<string, int> UnusableWithAValidator => Cross(["Speechmatics", "AssemblyAI", "Cartesia"], [0, -1, 601]);

    public static TheoryData<string, int?> UsableWithAValidator => CrossUsable(["Speechmatics", "AssemblyAI", "Cartesia"]);

    public static TheoryData<string, int> UnusableForEveryClient =>
        Cross(["Deepgram", "Speechmatics", "AssemblyAI", "Cartesia"], [0, -1, 601]);

    public static TheoryData<string, int?> UsableForEveryClient => CrossUsable(["Deepgram", "Speechmatics", "AssemblyAI", "Cartesia"]);

    public static TheoryData<string, int> MadeUnusable => Cross(["Deepgram", "AssemblyAI", "Cartesia", "Speechmatics"], [0, -1]);

    [Theory]
    [MemberData(nameof(UnusableWithAValidator))]
    public void Validate_ShouldFailNamingTheOption_WhenConnectTimeoutSecondsIsOutsideOneTo600(string client, int value)
    {
        var result = Validate(client, value);

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
        Validate(client, value).Succeeded.Should().BeTrue($"{value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "the default"} is usable");
    }

    [Theory]
    [MemberData(nameof(UnusableForEveryClient))]
    public async Task Ctor_ShouldThrowNamingTheOption_WhenConnectTimeoutSecondsIsOutsideOneTo600(string client, int value)
    {
        SpeechRecognizer? created = null;
        var fault = Record.Exception(() => created = Create(client, OptionsFor(client, value, port: 1)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeOfType<ArgumentOutOfRangeException>($"{value} s is not a usable connect timeout")
            .Which.ParamName.Should().Be(Option);
    }

    [Theory]
    [MemberData(nameof(UsableForEveryClient))]
    public async Task Ctor_ShouldSucceed_WhenConnectTimeoutSecondsIsInsideOneTo600(string client, int? value)
    {
        SpeechRecognizer? created = null;
        var fault = Record.Exception(() => created = Create(client, OptionsFor(client, value, port: 1)));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(MadeUnusable))]
    public async Task StreamAsync_ShouldThrowNamingTheOptionWithoutDialling_WhenConnectTimeoutSecondsWasMadeUnusableAfterConstruction(string client, int value)
    {
        // Arrange — a far end that would take the dial; the client is built with a usable value, which the
        // host then sets to an unusable value on the very object the client holds.
        await using var stalled = new StalledUpgradeListener();
        stalled.Start();
        var options = OptionsFor(client, null, stalled.Port);
        await using var recognizer = Create(client, options);
        SetConnectTimeout(options, value);

        // Act
        var fault = await Record.ExceptionAsync(() => TranscribeAsync(recognizer).WaitAsync(SignalTimeout));

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
    public async Task AddDeepgramSpeechRecognizer_ShouldRegisterAValidator_ThatRejectsAnUnusableConnectTimeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeepgramSpeechRecognizer(o =>
        {
            o.ApiKey = "test";
            o.ConnectTimeoutSeconds = 0;
        });
        await using var provider = services.BuildServiceProvider();

        var fault = Record.Exception(() => provider.GetRequiredService<IOptions<DeepgramOptions>>().Value);

        fault.Should().BeOfType<OptionsValidationException>("the registration validates Deepgram's options as it does its siblings'")
            .Which.Message.Should().Contain(Option);
    }

    private static ValidateOptionsResult Validate(string client, int? value) => client switch
    {
        "Speechmatics" => new SpeechmaticsOptionsValidator().Validate(null, (SpeechmaticsOptions)OptionsFor(client, value, port: 1).Value),
        "AssemblyAI" => new AssemblyAiOptionsValidator().Validate(null, (AssemblyAiOptions)OptionsFor(client, value, port: 1).Value),
        "Cartesia" => new CartesiaOptionsValidator().Validate(null, (CartesiaOptions)OptionsFor(client, value, port: 1).Value),
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    /// <summary>The options of <paramref name="client"/>, usable but for <paramref name="value"/> (null: the default).</summary>
    private static IOptions<object> OptionsFor(string client, int? value, int port) => client switch
    {
        "Deepgram" => Wrap(new DeepgramOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v1/listen",
            ConnectTimeoutSeconds = value ?? new DeepgramOptions().ConnectTimeoutSeconds,
        }),
        "AssemblyAI" => Wrap(new AssemblyAiOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v3/ws",
            ConnectTimeoutSeconds = value ?? new AssemblyAiOptions().ConnectTimeoutSeconds,
        }),
        "Cartesia" => Wrap(new CartesiaOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/stt/websocket",
            ConnectTimeoutSeconds = value ?? new CartesiaOptions().ConnectTimeoutSeconds,
        }),
        "Speechmatics" => Wrap(new SpeechmaticsOptions
        {
            ApiKey = "test-key",
            BaseUri = $"ws://127.0.0.1:{port}/v2",
            ConnectTimeoutSeconds = value ?? new SpeechmaticsOptions().ConnectTimeoutSeconds,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    private static IOptions<object> Wrap(object options) => Microsoft.Extensions.Options.Options.Create(options);

    private static SpeechRecognizer Create(string client, IOptions<object> options) => client switch
    {
        "Deepgram" => new DeepgramSpeechRecognizer(Microsoft.Extensions.Options.Options.Create((DeepgramOptions)options.Value)),
        "AssemblyAI" => new AssemblyAiSpeechRecognizer(Microsoft.Extensions.Options.Options.Create((AssemblyAiOptions)options.Value)),
        "Cartesia" => new CartesiaSpeechRecognizer(Microsoft.Extensions.Options.Options.Create((CartesiaOptions)options.Value)),
        "Speechmatics" => new SpeechmaticsSpeechRecognizer(Microsoft.Extensions.Options.Options.Create((SpeechmaticsOptions)options.Value)),
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    private static void SetConnectTimeout(IOptions<object> options, int value)
    {
        switch (options.Value)
        {
            case DeepgramOptions o: o.ConnectTimeoutSeconds = value; break;
            case AssemblyAiOptions o: o.ConnectTimeoutSeconds = value; break;
            case CartesiaOptions o: o.ConnectTimeoutSeconds = value; break;
            case SpeechmaticsOptions o: o.ConnectTimeoutSeconds = value; break;
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

    /// <summary>One stream to its end, on its own task.</summary>
    private static Task TranscribeAsync(SpeechRecognizer recognizer)
        => Task.Run(async () =>
        {
            await foreach (var _ in recognizer.StreamAsync(
                RecognizerEndOfInputPeers.Frames(5), AudioFormat.Slin16Mono8kHz, CancellationToken.None))
            {
                // Nothing is expected: the stream must fail before it dials.
            }
        });
}
