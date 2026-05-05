using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientStateTests
{
    private static AriClient CreateClient()
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        });
        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    [Fact]
    public async Task State_ShouldBeInitial_WhenNewClientCreated()
    {
        await using var sut = CreateClient();

        sut.State.Should().Be(AriConnectionState.Initial);
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_WhenNotConnected()
    {
        await using var sut = CreateClient();

        sut.IsConnected.Should().BeFalse();
    }
}
