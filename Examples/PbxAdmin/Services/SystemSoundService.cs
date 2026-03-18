using PbxAdmin.Models;

namespace PbxAdmin.Services;

public sealed class SystemSoundService
{
    private readonly IConfiguration _config;
    private readonly AudioFileService _audioSvc;
    private static readonly HashSet<string> SoundExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".gsm", ".ulaw", ".alaw", ".sln", ".g729", ".sln16" };

    public SystemSoundService(IConfiguration config, AudioFileService audioSvc)
    {
        _config = config;
        _audioSvc = audioSvc;
    }

    public string GetSoundsPath(string serverId)
        => _config[$"Asterisk:Servers:{serverId}:SoundsPath"] ?? "/var/lib/asterisk/sounds";

    public Task<List<SoundFileInfo>> GetSystemSoundsAsync(string serverId, string? subdirectory = null)
    {
        var basePath = GetSoundsPath(serverId);
        var searchPath = subdirectory is not null ? Path.Combine(basePath, subdirectory) : basePath;

        if (!Directory.Exists(searchPath))
            return Task.FromResult<List<SoundFileInfo>>([]);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SoundFileInfo>();

        foreach (var file in Directory.EnumerateFiles(searchPath, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!SoundExtensions.Contains(ext)) continue;

            var relativePath = Path.GetRelativePath(basePath, file);
            var category = Path.GetDirectoryName(relativePath) ?? "";
            var name = Path.GetFileNameWithoutExtension(file);
            var dedupeKey = $"{category}/{name}";

            // Deduplicate: same sound may exist in multiple formats
            if (!seen.Add(dedupeKey)) continue;

            results.Add(new SoundFileInfo(name, relativePath, ext.TrimStart('.'), new FileInfo(file).Length, category));
        }

        return Task.FromResult(results.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList());
    }

    public bool SoundExists(string serverId, string soundName)
    {
        var basePath = GetSoundsPath(serverId);
        if (!Directory.Exists(basePath)) return false;

        // Asterisk looks for sounds without extension — check if any format exists
        foreach (var ext in SoundExtensions)
        {
            if (File.Exists(Path.Combine(basePath, soundName + ext)))
                return true;
        }

        // Also check subdirectories (e.g., en/welcome)
        foreach (var ext in SoundExtensions)
        {
            var matches = Directory.EnumerateFiles(basePath, Path.GetFileName(soundName) + ext, SearchOption.AllDirectories);
            if (matches.Any()) return true;
        }

        return false;
    }

    public FileStream? GetSoundStream(string serverId, string relativePath)
    {
        var basePath = GetSoundsPath(serverId);

        // Can't use AudioFileService.GetStream() because it rejects '/' in filenames.
        // Do path validation manually (same containment check pattern).
        var fullPath = Path.Combine(basePath, relativePath);
        var resolved = Path.GetFullPath(fullPath);
        if (!resolved.StartsWith(Path.GetFullPath(basePath), StringComparison.Ordinal))
            return null;
        if (!File.Exists(resolved))
            return null;

        return new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
