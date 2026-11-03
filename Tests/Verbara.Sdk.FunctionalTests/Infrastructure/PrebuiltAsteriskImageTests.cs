using Verbara.Sdk.TestInfrastructure;
using FluentAssertions;

namespace Verbara.Sdk.FunctionalTests.Infrastructure;

/// <summary>
/// The lookup behind <c>ASTERISK_TEST_IMAGE</c>. No Docker and no category: it runs in the unit lane, with the host's
/// image lookup replaced by a function.
/// </summary>
public sealed class PrebuiltAsteriskImageTests
{
    private const string ContentId = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldReturnNull_WhenTheVariableIsUnsetOrBlank(string? value)
    {
        PrebuiltAsteriskImage.Normalize(value).Should().BeNull("an unset or blank variable leaves the Dockerfile build in place");
    }

    [Fact]
    public void Normalize_ShouldTrimTheName_WhenTheVariableHasSurroundingSpace()
    {
        PrebuiltAsteriskImage.Normalize("  local/asterisk-test:22 ").Should().Be("local/asterisk-test:22");
    }

    [Theory]
    [InlineData("local/asterisk-test:22")]
    [InlineData("local/asterisk-test@" + ContentId)]
    [InlineData(ContentId)]
    public async Task ResolveAsync_ShouldReturnTheImageByItsContentId_WhenTheHostHasIt(string reference)
    {
        var asked = new List<string>();

        var image = await PrebuiltAsteriskImage.ResolveAsync(reference, (r, _) =>
        {
            asked.Add(r);
            return Task.FromResult<string?>(ContentId);
        });

        asked.Should().Equal([reference], "the name is looked up as given: by tag, digest or id");
        image.FullName.Should().Be(ContentId, "the container must start the image that was looked up, not whatever a tag names later");
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrowNamingTheVariableAndTheImage_WhenTheHostHasNoImageByThatName()
    {
        var resolve = () => PrebuiltAsteriskImage.ResolveAsync("local/missing:1", (_, _) => Task.FromResult<string?>(null));

        (await resolve.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("ASTERISK_TEST_IMAGE is 'local/missing:1'")
            .And.Contain("never pull")
            .And.Contain("docker build -f docker/Dockerfile.asterisk");
    }
}
