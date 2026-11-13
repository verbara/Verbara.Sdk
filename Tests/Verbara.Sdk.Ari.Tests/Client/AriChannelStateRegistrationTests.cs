using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Verbara.Sdk.Ari.Resources;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// Where the channel-state reader is registered. It belongs to <see cref="AriChannelState"/> itself, so any
/// serializer reads Asterisk's spellings, and it is not in <see cref="AriJsonContext"/>'s options, where a single
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
}

/// <summary>A consumer's own context over the SDK's model, with no options at all.</summary>
[JsonSerializable(typeof(AriChannel))]
internal sealed partial class ConsumerAriJsonContext : JsonSerializerContext;
