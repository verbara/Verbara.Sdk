using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Verbara.Sdk.TestInfrastructure;

/// <summary>
/// An Asterisk test image the operator has already built, named by the <c>ASTERISK_TEST_IMAGE</c> environment
/// variable. With it set, the fixtures run that image as it is instead of building <c>docker/Dockerfile.asterisk</c>
/// through Testcontainers; unset or blank, nothing changes.
/// </summary>
/// <remarks>
/// <para>
/// Testcontainers pre-pulls every base image of a Dockerfile it builds, and decides what is missing by the local
/// images' tags alone. A base the build names by a content id, or by the digest of an image that exists only on this
/// host, is never one of those tags, so the pull runs and fails before any build starts. Building the image with
/// <c>docker build</c> and naming it here skips that pre-pull.
/// </para>
/// <para>
/// The image is looked up on the Docker host Testcontainers uses, by tag, digest or id, and is never pulled: a name
/// the host does not have fails here, before any container starts. The image is handed on by its content id, so the
/// container starts the image that was looked up even if a tag moves meanwhile.
/// </para>
/// </remarks>
public static class PrebuiltAsteriskImage
{
    /// <summary>The environment variable that names the prebuilt image.</summary>
    public const string Variable = "ASTERISK_TEST_IMAGE";

    /// <summary>The prebuilt image's name from the environment, or <see langword="null"/> when it is unset or blank.</summary>
    public static string? Configured() => Normalize(Environment.GetEnvironmentVariable(Variable));

    /// <summary>The trimmed <paramref name="value"/>, or <see langword="null"/> when it is null or blank.</summary>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The image <paramref name="reference"/> names, looked up on the Testcontainers Docker host.</summary>
    /// <exception cref="InvalidOperationException">The host has no image by that name.</exception>
    public static Task<IImage> ResolveAsync(string reference, CancellationToken ct = default) =>
        ResolveAsync(reference, InspectIdAsync, ct);

    /// <summary>The image <paramref name="reference"/> names, looked up with <paramref name="inspectId"/>.</summary>
    /// <param name="reference">A tag, a digest reference or a content id.</param>
    /// <param name="inspectId">The image's content id for a reference, or <see langword="null"/> when the host has none.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <exception cref="InvalidOperationException">The host has no image by that name.</exception>
    public static async Task<IImage> ResolveAsync(string reference, Func<string, CancellationToken, Task<string?>> inspectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(inspectId);

        var id = await inspectId(reference, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException(
                $"{Variable} is '{reference}', but the Docker host has no image by that tag, digest or id, and the "
                + "fixtures never pull it. Build it first, e.g. 'docker build -f docker/Dockerfile.asterisk docker/ "
                + "--build-arg ASTERISK_VERSION=<version> --build-arg ASTERISK_BASE_IMAGE=<base> -t <name>', or unset "
                + $"{Variable} to build the image from the pinned base.");

        return new DockerImage(id);
    }

    private static async Task<string?> InspectIdAsync(string reference, CancellationToken ct)
    {
        using var client = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder().Build();
        try
        {
            var image = await client.Images.InspectImageAsync(reference, ct).ConfigureAwait(false);
            return image.ID;
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }
}
