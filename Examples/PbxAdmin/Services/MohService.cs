using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

public sealed partial class MohService
{
    private readonly IMohClassRepository _repo;
    private readonly IRecordingMohSchemaManager _schema;
    private readonly IConfigProviderResolver _providerResolver;
    private readonly AudioFileService _audioSvc;
    private readonly ILogger<MohService> _logger;

    private const string ModeFiles = "files";
    private const string ModeMp3 = "mp3";
    private const string ModeCustom = "custom";

    private static readonly string[] ValidModes = [ModeFiles, ModeMp3, ModeCustom];

    public MohService(
        IMohClassRepository repo,
        IRecordingMohSchemaManager schema,
        IConfigProviderResolver providerResolver,
        AudioFileService audioSvc,
        ILogger<MohService> logger)
    {
        _repo = repo;
        _schema = schema;
        _providerResolver = providerResolver;
        _audioSvc = audioSvc;
        _logger = logger;
    }

    public async Task<List<MohClass>> GetClassesAsync(string serverId, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetAllAsync(serverId, ct);
    }

    public async Task<MohClass?> GetClassAsync(int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        return await _repo.GetByIdAsync(id, ct);
    }

    public async Task<(bool Success, string? Error)> CreateClassAsync(
        string serverId, MohClass mohClass, CancellationToken ct = default)
    {
        var error = ValidateClass(mohClass);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, mohClass.Name, ct);
        if (existing is not null) return (false, $"Class '{mohClass.Name}' already exists");

        mohClass.ServerId = serverId;
        var id = await _repo.InsertAsync(mohClass, ct);

        // Create directory if mode=files or mp3 and path is accessible
        if (mohClass.Mode is ModeFiles or ModeMp3 && !string.IsNullOrWhiteSpace(mohClass.Directory))
        {
            try { Directory.CreateDirectory(mohClass.Directory); }
            catch (Exception ex) { DirCreateFailed(_logger, ex, mohClass.Directory); }
        }

        await RegenerateMohConfAsync(serverId, ct);
        ClassCreated(_logger, id, mohClass.Name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateClassAsync(
        string serverId, MohClass mohClass, CancellationToken ct = default)
    {
        var error = ValidateClass(mohClass);
        if (error is not null) return (false, error);

        await _schema.EnsureSchemaAsync(ct);

        var existing = await _repo.GetByNameAsync(serverId, mohClass.Name, ct);
        if (existing is not null && existing.Id != mohClass.Id)
            return (false, $"Class '{mohClass.Name}' already exists");

        mohClass.ServerId = serverId;
        await _repo.UpdateAsync(mohClass, ct);
        await RegenerateMohConfAsync(serverId, ct);
        ClassUpdated(_logger, mohClass.Id);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteClassAsync(
        string serverId, int id, CancellationToken ct = default)
    {
        await _schema.EnsureSchemaAsync(ct);
        await _repo.DeleteAsync(id, ct);
        await RegenerateMohConfAsync(serverId, ct);
        ClassDeleted(_logger, id);
        return (true, null);
    }

    // --- Audio file management ---

    public async Task<List<AudioFileInfo>> GetAudioFilesAsync(int classId, CancellationToken ct = default)
    {
        var cls = await _repo.GetByIdAsync(classId, ct);
        if (cls is null) return [];
        return await _audioSvc.GetFilesAsync(cls.Directory, ct);
    }

    public async Task<(bool Success, string? Error)> UploadAudioAsync(
        int classId, string filename, Stream stream, long fileSize,
        int maxFileSizeMb, int maxClassSizeMb, CancellationToken ct = default)
    {
        if (!_audioSvc.IsValidFilename(filename))
            return (false, "Invalid filename");

        if (fileSize > maxFileSizeMb * 1024L * 1024L)
            return (false, $"File exceeds {maxFileSizeMb}MB limit");

        var (validMagic, detectedFormat) = _audioSvc.ValidateMagicBytes(stream);
        if (!validMagic)
            return (false, "Unrecognized audio format");

        var cls = await _repo.GetByIdAsync(classId, ct);
        if (cls is null) return (false, "Class not found");

        // Check class total size
        var existingFiles = await _audioSvc.GetFilesAsync(cls.Directory, ct);
        var totalSize = existingFiles.Sum(f => f.Size) + fileSize;
        if (totalSize > maxClassSizeMb * 1024L * 1024L)
            return (false, $"Class total would exceed {maxClassSizeMb}MB limit");

        // Determine if conversion is needed
        var nativeFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "wav", "gsm", "sln", "ulaw", "alaw" };
        var needsConversion = !nativeFormats.Contains(detectedFormat);

        var targetPath = Path.Combine(cls.Directory, filename);

        if (needsConversion && _audioSvc.IsSoxAvailable())
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                await using (var tempFile = File.Create(tempPath))
                    await stream.CopyToAsync(tempFile, ct);

                var wavFilename = Path.ChangeExtension(filename, ".wav");
                var wavPath = Path.Combine(cls.Directory, wavFilename);

                var (ok, soxError) = await _audioSvc.ConvertWithSoxAsync(tempPath, wavPath, ct);
                if (!ok) return (false, soxError);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
        else if (needsConversion)
        {
            return (false, $"Format '{detectedFormat}' requires sox for conversion, but sox is not available");
        }
        else
        {
            Directory.CreateDirectory(cls.Directory);
            await using var fs = File.Create(targetPath);
            await stream.CopyToAsync(fs, ct);
        }

        FileUploaded(_logger, filename, classId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAudioAsync(
        int classId, string filename, CancellationToken ct = default)
    {
        if (!_audioSvc.IsValidFilename(filename))
            return (false, "Invalid filename");

        var cls = await _repo.GetByIdAsync(classId, ct);
        if (cls is null) return (false, "Class not found");

        var path = Path.Combine(cls.Directory, filename);
        if (!File.Exists(path)) return (false, "File not found");

        File.Delete(path);
        FileDeleted(_logger, filename, classId);
        return (true, null);
    }

    public async Task<FileStream?> GetAudioStreamAsync(int classId, string filename, CancellationToken ct = default)
    {
        var cls = await _repo.GetByIdAsync(classId, ct);
        if (cls is null) return null;
        return _audioSvc.GetStream(cls.Directory, filename);
    }

    // --- Config regeneration ---

    public async Task RegenerateMohConfAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var provider = _providerResolver.GetProvider(serverId);
            var classes = await _repo.GetAllAsync(serverId, ct);

            // Delete all existing sections
            var categories = await provider.GetCategoriesAsync(serverId, "musiconhold.conf", ct);
            foreach (var cat in categories)
                await provider.DeleteSectionAsync(serverId, "musiconhold.conf", cat.Name, ct);

            // Recreate from DB
            foreach (var cls in classes)
            {
                var lines = new List<KeyValuePair<string, string>>
                {
                    new("mode", cls.Mode),
                };

                if (cls.Mode is ModeFiles or ModeMp3)
                {
                    lines.Add(new("directory", cls.Directory));
                    lines.Add(new("sort", cls.Sort));
                }
                else if (cls.Mode == ModeCustom && cls.CustomApplication is not null)
                {
                    lines.Add(new("application", cls.CustomApplication));
                }

                await provider.CreateSectionWithLinesAsync(serverId, "musiconhold.conf", cls.Name, lines, ct);
            }

            // Reload module
            await provider.ExecuteCommandAsync(serverId, "module reload res_musiconhold", ct);
            ConfRegenerated(_logger, serverId, classes.Count);
        }
        catch (Exception ex)
        {
            ConfRegenFailed(_logger, ex, serverId);
        }
    }

    // --- Static conf generator (for testing) ---

    public static string GenerateMohConf(List<MohClass> classes)
    {
        var sb = new StringBuilder();
        foreach (var cls in classes)
        {
            sb.Append('[').Append(cls.Name).AppendLine("]");
            sb.Append("mode=").AppendLine(cls.Mode);

            if (cls.Mode is ModeFiles or ModeMp3)
            {
                sb.Append("directory=").AppendLine(cls.Directory);
                sb.Append("sort=").AppendLine(cls.Sort);
            }
            else if (cls.Mode == ModeCustom && cls.CustomApplication is not null)
            {
                sb.Append("application=").AppendLine(cls.CustomApplication);
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    // --- Validation ---

    private static string? ValidateClass(MohClass mohClass)
    {
        if (string.IsNullOrWhiteSpace(mohClass.Name))
            return "Name is required";
        if (!MohNameRegex().IsMatch(mohClass.Name))
            return "Name must contain only letters, numbers, hyphens, and underscores";
        if (!ValidModes.Contains(mohClass.Mode, StringComparer.OrdinalIgnoreCase))
            return $"Mode must be one of: {string.Join(", ", ValidModes)}";
        if (mohClass.Mode is ModeFiles or ModeMp3 && string.IsNullOrWhiteSpace(mohClass.Directory))
            return "Directory is required for files/mp3 mode";
        return null;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex MohNameRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created MOH class {Id}: {Name}")]
    private static partial void ClassCreated(ILogger logger, int id, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated MOH class {Id}")]
    private static partial void ClassUpdated(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted MOH class {Id}")]
    private static partial void ClassDeleted(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create directory {Path}")]
    private static partial void DirCreateFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Uploaded {FileName} to MOH class {ClassId}")]
    private static partial void FileUploaded(ILogger logger, string fileName, int classId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {FileName} from MOH class {ClassId}")]
    private static partial void FileDeleted(ILogger logger, string fileName, int classId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Regenerated musiconhold.conf for {ServerId}: {Count} classes")]
    private static partial void ConfRegenerated(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to regenerate musiconhold.conf for {ServerId}")]
    private static partial void ConfRegenFailed(ILogger logger, Exception ex, string serverId);
}
