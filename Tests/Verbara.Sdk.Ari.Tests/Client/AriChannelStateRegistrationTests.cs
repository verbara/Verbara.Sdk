using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Verbara.Sdk.Ari.Resources;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// Where the channel-state reader is registered. It belongs to <see cref="AriChannelState"/> itself, so a serializer
/// whose options list no enum converter of their own reads Asterisk's spellings, and it is not in <see cref="AriJsonContext"/>'s options, where a single
/// converter turns off the source-generated write path of every type in the context.
/// </summary>
public sealed class AriChannelStateRegistrationTests
{
    [Fact]
    public void AriJsonContext_ShouldCarryNoConverterInItsOptions()
    {
        AriJsonContext.Default.Options.Converters.Should().BeEmpty();
    }

    [Fact]
    public void AriJsonContext_ShouldKeepTheGeneratedWriter_ForAChannelABridgeAndAnEventCarryingAChannel()
    {
        AriJsonContext.Default.AriChannel.SerializeHandler.Should().NotBeNull();
        AriJsonContext.Default.AriBridge.SerializeHandler.Should().NotBeNull();
        AriJsonContext.Default.StasisStartEvent.SerializeHandler.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Pre-ring", AriChannelState.PreRing)]
    [InlineData("Dialing Offhook", AriChannelState.DialingOffhook)]
    [InlineData("Unknown (7)", AriChannelState.Unknown)]
    [InlineData("Up", AriChannelState.Up)]
    public void AConsumersOwnContext_ShouldReadAsterisksSpelling_WithNoConverterOption(string wire, AriChannelState expected)
    {
        var channel = JsonSerializer.Deserialize(
            $$"""{"Id":"c","State":"{{wire}}","Name":"PJSIP/x"}""", ConsumerAriJsonContext.Default.AriChannel);

        channel!.State.Should().Be(expected);
        channel.Name.Should().Be("PJSIP/x");
    }

    [Fact]
    public void AConsumersOwnContext_ShouldWriteAsterisksSpelling()
    {
        var json = JsonSerializer.Serialize(
            new AriChannel { Id = "c", State = AriChannelState.PreRing }, ConsumerAriJsonContext.Default.AriChannel);

        json.Should().Contain("\"State\":\"Pre-ring\"");
    }

    [Theory]
    [InlineData("\"Pre-ring\"", AriChannelState.PreRing)]
    [InlineData("\"Unknown (7)\"", AriChannelState.Unknown)]
    [InlineData("\"Mute\"", AriChannelState.Mute)]
    [InlineData("6", AriChannelState.Up)]
    public void AriJsonContext_ShouldReadAsterisksSpelling_WhenTheStateIsTheRoot(string json, AriChannelState expected)
    {
        JsonSerializer.Deserialize(json, AriJsonContext.Default.AriChannelState).Should().Be(expected);
    }

    [Fact]
    public void AConsumersOwnContext_ShouldWriteAsterisksSpelling_WhenTheStateIsADictionaryKey()
    {
        var states = new Dictionary<AriChannelState, int>
        {
            [AriChannelState.PreRing] = 1,
            [AriChannelState.DialingOffhook] = 2,
            [AriChannelState.Up] = 3,
        };

        var json = JsonSerializer.Serialize(states, ConsumerAriJsonContext.Default.DictionaryAriChannelStateInt32);

        json.Should().Be("""{"Pre-ring":1,"Dialing Offhook":2,"Up":3}""");
    }

    [Theory]
    [InlineData("Pre-ring", AriChannelState.PreRing)]
    [InlineData("Dialing Offhook", AriChannelState.DialingOffhook)]
    [InlineData("Rsrved", AriChannelState.Rsrvd)]
    [InlineData("PreRing", AriChannelState.PreRing)]
    [InlineData("up", AriChannelState.Up)]
    [InlineData("Unknown (7)", AriChannelState.Unknown)]
    [InlineData("6", AriChannelState.Up)]
    [InlineData("42", AriChannelState.Unknown)]
    [InlineData("-1", AriChannelState.Unknown)]
    public void AConsumersOwnContext_ShouldReadAsterisksSpelling_WhenTheStateIsADictionaryKey(
        string key, AriChannelState expected)
    {
        var states = JsonSerializer.Deserialize(
            $$"""{"{{key}}":5}""", ConsumerAriJsonContext.Default.DictionaryAriChannelStateInt32);

        states.Should().ContainSingle().Which.Should().Be(new KeyValuePair<AriChannelState, int>(expected, 5));
    }

    [Fact]
    public void OptionsWithTheirOwnEnumConverter_ShouldWriteTheMemberNameAndRefuseAsterisksSpelling()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = ConsumerAriJsonContext.Default,
            Converters = { new JsonStringEnumConverter<AriChannelState>() },
        };
        var channelInfo = (JsonTypeInfo<AriChannel>)options.GetTypeInfo(typeof(AriChannel));

        JsonSerializer.Serialize(new AriChannel { Id = "c", State = AriChannelState.PreRing }, channelInfo)
            .Should().Contain("\"State\":\"PreRing\"");
        var read = () => JsonSerializer.Deserialize("""{"Id":"c","State":"Pre-ring"}""", channelInfo);
        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void OptionsWithTheirOwnEnumConverter_ShouldReadAsterisksSpelling_WhenTheStateConverterIsInsertedFirst()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = ConsumerAriJsonContext.Default,
            Converters = { new AriChannelStateConverter(), new JsonStringEnumConverter<AriChannelState>() },
        };

        var channelInfo = (JsonTypeInfo<AriChannel>)options.GetTypeInfo(typeof(AriChannel));

        var channel = JsonSerializer.Deserialize("""{"Id":"c","State":"Pre-ring"}""", channelInfo);

        channel!.State.Should().Be(AriChannelState.PreRing);
        JsonSerializer.Serialize(channel, channelInfo).Should().Contain("\"State\":\"Pre-ring\"");
    }
}

/// <summary>A consumer's own context over the SDK's model, with no options at all.</summary>
[JsonSerializable(typeof(AriChannel))]
[JsonSerializable(typeof(Dictionary<AriChannelState, int>))]
internal sealed partial class ConsumerAriJsonContext : JsonSerializerContext;
