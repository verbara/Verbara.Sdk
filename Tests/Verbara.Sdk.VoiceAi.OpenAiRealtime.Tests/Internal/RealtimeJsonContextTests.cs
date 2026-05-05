using System.Text.Json;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

public class RealtimeJsonContextTests
{
    [Fact]
    public void ServerEventBase_DeserializesType()
    {
        const string json = """{"type":"response.created"}""";
        var evt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase)!;
        evt.Type.Should().Be("response.created");
    }
}
