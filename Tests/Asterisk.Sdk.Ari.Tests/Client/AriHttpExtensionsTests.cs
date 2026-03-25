using System.Net;
using Asterisk.Sdk.Ari;
using Asterisk.Sdk.Ari.Client;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Client;

public sealed class AriHttpExtensionsTests
{
    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldNotThrow_WhenSuccessStatusCode()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        var act = async () => await response.EnsureAriSuccessAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldNotThrow_WhenNoContent()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);

        var act = async () => await response.EnsureAriSuccessAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldThrowAriNotFoundException_When404()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Channel not found")
        };

        var act = async () => await response.EnsureAriSuccessAsync();

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Channel not found");
        ex.Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldThrowAriConflictException_When409()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("Bridge already exists")
        };

        var act = async () => await response.EnsureAriSuccessAsync();

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("Bridge already exists");
        ex.Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldThrowAriException_When500()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        };

        var act = async () => await response.EnsureAriSuccessAsync();

        var ex = await act.Should().ThrowAsync<AriException>();
        ex.Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldThrowAriException_When422()
    {
        var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("Invalid parameters")
        };

        var act = async () => await response.EnsureAriSuccessAsync();

        var ex = await act.Should().ThrowAsync<AriException>();
        ex.Which.StatusCode.Should().Be(422);
        ex.Which.Message.Should().Contain("Invalid parameters");
    }

    [Fact]
    public async Task EnsureAriSuccessAsync_ShouldThrowAriException_When400()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad request")
        };

        var act = async () => await response.EnsureAriSuccessAsync();

        await act.Should().ThrowAsync<AriException>()
            .Where(e => e.StatusCode == 400);
    }
}
