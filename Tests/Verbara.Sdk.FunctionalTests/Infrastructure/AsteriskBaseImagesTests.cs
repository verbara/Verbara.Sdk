using Verbara.Sdk.TestInfrastructure;
using FluentAssertions;

namespace Verbara.Sdk.FunctionalTests.Infrastructure;

/// <summary>
/// The table reader the Asterisk image build goes through. No Docker and no category: it runs in the unit lane, so a
/// version that loses its pinned base fails there, before any functional leg builds an image.
/// </summary>
public sealed class AsteriskBaseImagesTests
{
    private const string Table = """
        # comment
        20 example/pbx:20.1.0_build-a@sha256:0000000000000000000000000000000000000000000000000000000000000020
        22 example/pbx:22.1.0_build-b@sha256:0000000000000000000000000000000000000000000000000000000000000022

        23 example/pbx:23.1.0_build-c@sha256:0000000000000000000000000000000000000000000000000000000000000023
        """;

    [Fact]
    public void Resolve_ShouldReturnTheVersionsLine_WhenTheTablePinsIt()
    {
        AsteriskBaseImages.Resolve("23", Table, "t.txt").Should().Be(
            "example/pbx:23.1.0_build-c@sha256:0000000000000000000000000000000000000000000000000000000000000023");
    }

    [Fact]
    public void Resolve_ShouldThrowNamingTheFileAndTheKnownVersions_WhenTheVersionHasNoLine()
    {
        var resolve = () => AsteriskBaseImages.Resolve("21", Table, "docker/t.txt");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("docker/t.txt pins no Asterisk base for ASTERISK_VERSION '21'; it pins 20, 22, 23.*");
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenTheCheckoutTableHasNoLineForTheVersion()
    {
        var resolve = () => AsteriskBaseImages.Resolve("99");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*asterisk-base-images.txt pins no Asterisk base for ASTERISK_VERSION '99'; it pins 20, 22, 23.*");
    }

    [Theory]
    [InlineData("22", ":22.")]
    [InlineData("23", ":23.")]
    public void Resolve_ShouldReturnAPinnedBuildOfTheVersion_WhenTheMatrixVersionIsRead(string version, string tag)
    {
        var reference = AsteriskBaseImages.Resolve(version);

        reference.Should().Contain(tag).And.MatchRegex("@sha256:[0-9a-f]{64}$");
    }
}
