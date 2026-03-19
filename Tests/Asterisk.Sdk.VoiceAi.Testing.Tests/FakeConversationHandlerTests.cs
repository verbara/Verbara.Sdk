using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeConversationHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldReturnConfiguredResponse()
    {
        var fake = new FakeConversationHandler().WithResponse("respuesta");
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        var result = await fake.HandleAsync("hola", ctx);
        result.Should().Be("respuesta");
    }

    [Fact]
    public async Task HandleAsync_ShouldCycleResponses()
    {
        var fake = new FakeConversationHandler().WithResponses(["uno", "dos"]);
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        var r1 = await fake.HandleAsync("a", ctx);
        var r2 = await fake.HandleAsync("b", ctx);
        var r3 = await fake.HandleAsync("c", ctx);
        r1.Should().Be("uno");
        r2.Should().Be("dos");
        r3.Should().Be("uno"); // cycles
    }

    [Fact]
    public async Task HandleAsync_ShouldTrackReceivedTranscripts()
    {
        var fake = new FakeConversationHandler().WithResponse("ok");
        var ctx = new ConversationContext { ChannelId = Guid.NewGuid() };
        await fake.HandleAsync("transcript1", ctx);
        await fake.HandleAsync("transcript2", ctx);
        fake.ReceivedTranscripts.Should().Equal("transcript1", "transcript2");
    }
}
