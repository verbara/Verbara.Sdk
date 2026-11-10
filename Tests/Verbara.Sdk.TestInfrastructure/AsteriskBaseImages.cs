namespace Verbara.Sdk.TestInfrastructure;

/// <summary>
/// The pinned Asterisk base image per version, read from <c>docker/asterisk-base-images.txt</c>: one
/// <c>&lt;version&gt; &lt;image&gt;:&lt;build tag&gt;@&lt;digest&gt;</c> line per supported version, <c>#</c> comments.
/// </summary>
/// <remarks>
/// A version alone does not name a server: each line pins one build by its per-build tag and digest. The 22, 23 and
/// 24 lines are this repository's own base (<c>.github/workflows/asterisk-base.yml</c>), whose tags are never rebuilt
/// over; the 20 control line is a third-party build whose version tags are re-published weekly.
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

    /// <summary>
    /// The Asterisk version a pinned base reference carries, read from its tag: <c>22.12.0</c> from
    /// <c>image:22.12.0_build@sha256:…</c> and from <c>image:22.12.0@sha256:…</c>, <c>22.12.0-rc2</c> from
    /// <c>image:22.12.0-rc2@sha256:…</c>.
    /// </summary>
    /// <param name="reference">An <c>&lt;image&gt;:&lt;tag&gt;[@&lt;digest&gt;]</c> reference, as the table pins it.</param>
    public static string PinnedVersion(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var at = reference.IndexOf('@', StringComparison.Ordinal);
        var named = at < 0 ? reference : reference[..at];
        var tag = named[(named.LastIndexOf(':') + 1)..];
        var underscore = tag.IndexOf('_', StringComparison.Ordinal);
        return underscore < 0 ? tag : tag[..underscore];
    }

    private static Dictionary<string, string> Parse(string table)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in table.Split('\n').Select(raw => raw.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                lines[parts[0]] = parts[1];
        }

        return lines;
    }
}
