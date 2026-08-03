namespace Verbara.Sdk.TestInfrastructure.Http;

/// <summary>
/// Locates and reads the captured provider responses a suite keeps in its <c>Recordings/</c> tree
/// and hands them to <see cref="HttpProviderMockServer"/> as stub bodies (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Discovery walks up from <see cref="AppContext.BaseDirectory"/> — a RUNTIME path
/// (<c>Tests/&lt;suite&gt;/bin/&lt;cfg&gt;/&lt;tfm&gt;</c>) — until it finds the folder, stopping at
/// the suite's project directory. That handles both layouts: recordings copied to the output
/// directory and recordings left in the source tree. <c>[CallerFilePath]</c> is deliberately NOT
/// used: this repo sets <c>ContinuousIntegrationBuild=true</c>, which remaps compile-time paths to
/// a non-existent <c>/_/…</c> in CI.
/// </para>
/// </remarks>
public sealed class ProviderRecordings
{
    /// <summary>Conventional folder name for a suite's captures.</summary>
    public const string DefaultFolderName = "Recordings";

    private ProviderRecordings(string directory) => Directory = directory;

    /// <summary>Absolute path of the resolved recordings folder.</summary>
    public string Directory { get; }

    /// <summary>
    /// Find the calling suite's recordings folder. Throws when it does not exist, so a typo or a
    /// missing <c>CopyToOutputDirectory</c> fails loudly instead of silently serving nothing.
    /// </summary>
    public static ProviderRecordings Locate(string folderName = DefaultFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, folderName);
            if (System.IO.Directory.Exists(candidate))
                return new ProviderRecordings(candidate);

            // The suite's project directory is the top of the search — beyond it we would start
            // matching another suite's captures.
            if (probe.EnumerateFiles("*.csproj").Any())
                break;

            probe = probe.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No '{folderName}' folder found walking up from '{AppContext.BaseDirectory}' to the " +
            "suite's project directory. Add the capture under the suite's " +
            $"'{folderName}/' tree (see docs/guides/ for the capture and redaction protocol).");
    }

    /// <summary>Use an explicit folder instead of discovery.</summary>
    public static ProviderRecordings At(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!System.IO.Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Recordings folder '{directory}' does not exist.");

        return new ProviderRecordings(Path.GetFullPath(directory));
    }

    /// <summary>Read a captured text/JSON response.</summary>
    public string ReadText(string relativePath) => File.ReadAllText(Resolve(relativePath));

    /// <summary>Read a captured binary response.</summary>
    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

    /// <summary>Absolute path of a capture, verified to exist.</summary>
    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalised = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.Combine(Directory, normalised);

        if (!File.Exists(full))
            throw new FileNotFoundException($"Recording '{relativePath}' not found under '{Directory}'.", full);

        return full;
    }
}
