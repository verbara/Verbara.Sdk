namespace Verbara.Sdk.TestInfrastructure;

/// <summary>
/// The pinned Asterisk base image per version, read from <c>docker/asterisk-base-images.txt</c>: one
/// <c>&lt;version&gt; &lt;image&gt;:&lt;build tag&gt;@&lt;digest&gt;</c> line per supported version, <c>#</c> comments.
/// </summary>
/// <remarks>
/// The publisher re-publishes each version tag weekly with a new build, so a version alone does not name a server.
/// A version with no line fails here, before any image is built, rather than fall back to a tag.
/// </remarks>
public static class AsteriskBaseImages
{
    /// <summary>The table's path in the checkout.</summary>
    public static string TablePath => Path.Join(DockerPaths.DockerDir, "asterisk-base-images.txt");

    /// <summary>The pinned base for <paramref name="asteriskVersion"/>, read from the checkout's table.</summary>
    /// <exception cref="InvalidOperationException">The table has no line for the version.</exception>
    public static string Resolve(string asteriskVersion) =>
        Resolve(asteriskVersion, File.ReadAllText(TablePath), TablePath);

    /// <summary>The pinned base for <paramref name="asteriskVersion"/> in <paramref name="table"/>.</summary>
    /// <param name="asteriskVersion">The <c>ASTERISK_VERSION</c> build argument, such as <c>22</c>.</param>
    /// <param name="table">The table's text.</param>
    /// <param name="tablePath">Where the table was read from, for the message.</param>
    /// <exception cref="InvalidOperationException">The table has no line for the version.</exception>
    public static string Resolve(string asteriskVersion, string table, string tablePath)
    {
        ArgumentNullException.ThrowIfNull(asteriskVersion);
        ArgumentNullException.ThrowIfNull(table);

        var lines = Parse(table);
        if (lines.TryGetValue(asteriskVersion, out var reference))
            return reference;

        var known = lines.Count == 0 ? "none" : string.Join(", ", lines.Keys.Order(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"{tablePath} pins no Asterisk base for ASTERISK_VERSION '{asteriskVersion}'; it pins {known}. "
            + "Add a '<version> <image>:<build tag>@<digest>' line, measured first, to build that version.");
    }

    private static Dictionary<string, string> Parse(string table)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in table.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                lines[parts[0]] = parts[1];
        }

        return lines;
    }
}
