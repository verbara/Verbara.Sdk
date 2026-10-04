using System.Text.RegularExpressions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Detector for the functional lane's image pins: every reference to the Asterisk base image or the SIPp image under
/// <c>docker/</c>, <c>.github/workflows/</c> and <c>Tests/</c> carries a registry digest, every pinned Asterisk base is
/// a line of <c>docker/asterisk-base-images.txt</c>, and <c>docker/Dockerfile.asterisk</c> defaults to that table's
/// 22 line and refuses a base of another version.
/// </summary>
/// <remarks>
/// <para>
/// A tag names whatever its publisher serves that day: the Asterisk 22 tag moved from 22.9.0 to 22.10.1 and is
/// rebuilt weekly, so a lane that pulls it tests a different server from one week to the next without any change in
/// the tree. A digest names one image.
/// </para>
/// <para>
/// The walk reads the file system rather than asking git, so a file a contributor has just created and not yet added
/// is read too. Build output (<c>bin</c>, <c>obj</c>, <c>TestResults</c>), nested checkouts and symbolic links are
/// skipped, and so are files that are not text (larger than 1 MiB, or holding a NUL byte).
/// </para>
/// <para>
/// The image names are assembled from parts, so this file and its tests never hold a reference the walk would find.
/// </para>
/// </remarks>
internal static partial class ImagePinScanner
{
    /// <summary>The Asterisk base image.</summary>
    public static readonly string AsteriskImage = "andrius" + "/" + "asterisk";

    /// <summary>The SIPp image.</summary>
    public static readonly string SippImage = "ctaloi" + "/" + "sipp";

    /// <summary>The table of pinned Asterisk bases, relative to the repository root.</summary>
    public const string TablePath = "docker/asterisk-base-images.txt";

    /// <summary>The Asterisk image's Dockerfile, relative to the repository root.</summary>
    public const string DockerfilePath = "docker/Dockerfile.asterisk";

    /// <summary>The version whose line the Dockerfile's default base must be.</summary>
    public const string DefaultVersion = "22";

    /// <summary>The directories scanned, relative to the repository root.</summary>
    public static readonly IReadOnlyList<string> ScannedDirectories = ["docker", ".github/workflows", "Tests"];

    private const long MaximumFileBytes = 1024 * 1024;

    /// <summary>One image reference found in a file.</summary>
    public sealed record Reference(string Path, int Line, string Image, string Text)
    {
        public bool IsPinned => DigestPattern().IsMatch(Text);

        public override string ToString() => $"{Path}:{Line}: {Text}";
    }

    /// <summary>The outcome of a scan: every reference found, and every violation as a line of text.</summary>
    public sealed record Report(IReadOnlyList<Reference> References, IReadOnlyList<string> Violations)
    {
        public bool IsClean => Violations.Count == 0;

        public override string ToString() => Violations.Count == 0
            ? $"clean: {References.Count} references, all pinned"
            : string.Join(Environment.NewLine, Violations);
    }

    /// <summary>Scans the checkout rooted at <paramref name="repoRoot"/>.</summary>
    public static Report Scan(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"The repository root '{repoRoot}' does not exist.");

        var references = new List<Reference>();
        foreach (var file in EnumerateFiles(repoRoot))
            references.AddRange(FindReferences(File.ReadAllText(file), ToRelative(repoRoot, file)));

        var violations = new List<string>();
        foreach (var image in new[] { AsteriskImage, SippImage })
        {
            if (!references.Any(r => r.Image == image))
                violations.Add($"found no reference to {image} under {string.Join(", ", ScannedDirectories)}: the walk read nothing, so it proves nothing");
        }

        foreach (var reference in references.Where(r => !r.IsPinned))
            violations.Add($"{reference}: no @sha256 digest — a tag names whatever its publisher serves that day");

        var tablePath = Path.Join(repoRoot, TablePath);
        IReadOnlyDictionary<string, string>? table = null;
        if (!File.Exists(tablePath))
            violations.Add($"{TablePath}: missing — the table of pinned Asterisk bases, one '<version> <reference>' line per version");
        else
            table = ReadTable(File.ReadAllText(tablePath), violations);

        if (table is not null)
        {
            var pinnedBases = new HashSet<string>(table.Values, StringComparer.Ordinal);
            foreach (var reference in references.Where(r => r.Image == AsteriskImage && r.IsPinned && r.Path != TablePath))
            {
                if (!pinnedBases.Contains(reference.Text))
                    violations.Add($"{reference}: a pinned Asterisk base that is not a line of {TablePath}");
            }
        }

        CheckDockerfile(repoRoot, table, violations);
        return new Report(references, violations);
    }

    /// <summary>Every reference to either image in <paramref name="text"/>, with its 1-based line.</summary>
    public static IEnumerable<Reference> FindReferences(string text, string path)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var image in new[] { AsteriskImage, SippImage })
            {
                var at = 0;
                while ((at = lines[i].IndexOf(image, at, StringComparison.Ordinal)) >= 0)
                {
                    var end = at + image.Length;
                    var suffix = ReferenceSuffixPattern().Match(lines[i], end);
                    var length = image.Length + (suffix.Success && suffix.Index == end ? suffix.Length : 0);
                    yield return new Reference(path, i + 1, image, lines[i].Substring(at, length));
                    at = end;
                }
            }
        }
    }

    /// <summary>
    /// Reads the table: one <c>&lt;version&gt; &lt;reference&gt;</c> per line, <c>#</c> comments and blank lines
    /// skipped. Adds a violation for a malformed line, a duplicate version, a reference without a digest, a reference
    /// to another image, and a tag that does not start with its line's version.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadTable(string text, List<string> violations)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var where = $"{TablePath}:{i + 1}";
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                violations.Add($"{where}: '{line}' is not '<version> <reference>'");
                continue;
            }

            var (version, reference) = (parts[0], parts[1]);
            if (!table.TryAdd(version, reference))
                violations.Add($"{where}: version {version} has a second line");

            var tagPrefix = AsteriskImage + ":" + version + ".";
            if (!reference.StartsWith(tagPrefix, StringComparison.Ordinal))
                violations.Add($"{where}: the reference for {version} does not start with '{tagPrefix}'");

            if (!DigestPattern().IsMatch(reference))
                violations.Add($"{where}: the reference for {version} has no @sha256 digest");
        }

        if (table.Count == 0)
            violations.Add($"{TablePath}: holds no line");

        return table;
    }

    private static void CheckDockerfile(string repoRoot, IReadOnlyDictionary<string, string>? table, List<string> violations)
    {
        var dockerfile = Path.Join(repoRoot, DockerfilePath);
        if (!File.Exists(dockerfile))
        {
            violations.Add($"{DockerfilePath}: missing");
            return;
        }

        var lines = File.ReadAllLines(dockerfile);
        var defaultLine = Array.FindIndex(lines, l => l.TrimStart().StartsWith("ARG ASTERISK_BASE_IMAGE=", StringComparison.Ordinal));
        if (defaultLine < 0)
        {
            violations.Add($"{DockerfilePath}: no 'ARG ASTERISK_BASE_IMAGE=<the {DefaultVersion} line of {TablePath}>' default");
        }
        else if (table is not null)
        {
            var value = lines[defaultLine].Trim()["ARG ASTERISK_BASE_IMAGE=".Length..];
            if (!table.TryGetValue(DefaultVersion, out var expected))
                violations.Add($"{TablePath}: no {DefaultVersion} line, which the Dockerfile's default base must be");
            else if (!string.Equals(value, expected, StringComparison.Ordinal))
                violations.Add($"{DockerfilePath}:{defaultLine + 1}: the default base '{value}' is not the {DefaultVersion} line of {TablePath} ('{expected}')");
        }

        var fromLine = Array.FindIndex(lines, l => l.Trim() == "FROM ${ASTERISK_BASE_IMAGE}");
        if (fromLine < 0)
            violations.Add($"{DockerfilePath}: no 'FROM ${{ASTERISK_BASE_IMAGE}}'");

        var checkLine = Array.FindIndex(lines, Math.Max(fromLine, 0), l => VersionCheckPattern().IsMatch(l));
        if (fromLine >= 0 && checkLine < 0)
            violations.Add($"{DockerfilePath}: no base-version check after the FROM (a RUN that requires 'asterisk -V' to start with 'Asterisk ${{ASTERISK_VERSION}}.' and exits 1 otherwise)");
    }

    private static List<string> EnumerateFiles(string repoRoot)
    {
        var files = new List<string>();
        foreach (var scanned in ScannedDirectories)
        {
            var start = Path.Join(repoRoot, scanned);
            if (!Directory.Exists(start))
                continue;

            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var info = new FileInfo(file);
                    if (info.LinkTarget is null && info.Length <= MaximumFileBytes && !HoldsNul(file))
                        files.Add(file);
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name is "bin" or "obj" or "TestResults" or ".git")
                        continue;
                    if (File.Exists(Path.Join(child, ".git")) || Directory.Exists(Path.Join(child, ".git")))
                        continue;
                    if (new DirectoryInfo(child).LinkTarget is not null)
                        continue;

                    pending.Push(child);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool HoldsNul(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                return true;
        }

        return false;
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex(@"@sha256:[0-9a-f]{64}")]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"\G[:@][A-Za-z0-9_.\-:@${}]*")]
    private static partial Regex ReferenceSuffixPattern();

    [GeneratedRegex(@"^\s*RUN\s+asterisk -V\b.*\^Asterisk \$\{ASTERISK_VERSION\}\\\..*exit 1")]
    private static partial Regex VersionCheckPattern();
}
