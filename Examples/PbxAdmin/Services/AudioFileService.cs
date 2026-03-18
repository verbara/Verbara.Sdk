// Examples/PbxAdmin/Services/AudioFileService.cs
using System.Diagnostics;
using System.Text.RegularExpressions;
using PbxAdmin.Models;

namespace PbxAdmin.Services;

public sealed partial class AudioFileService
{
    private readonly ILogger<AudioFileService> _logger;
    private readonly Lazy<bool> _soxAvailable;

    public AudioFileService(ILogger<AudioFileService> logger)
    {
        _logger = logger;
        _soxAvailable = new Lazy<bool>(CheckSox);
    }

    public bool IsSoxAvailable() => _soxAvailable.Value;

    public (bool Valid, string Format) ValidateMagicBytes(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        var bytesRead = stream.Read(header);
        stream.Position = 0;

        if (bytesRead < 4) return (false, "unknown");

        // WAV: RIFF....WAVE
        if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
            && bytesRead >= 12 && header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
            return (true, "wav");

        // OGG: OggS
        if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
            return (true, "ogg");

        // MP3: ID3 tag
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            return (true, "mp3");

        // MP3: sync bits 0xFF 0xFB/0xFA/0xF3/0xF2
        if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
            return (true, "mp3");

        // GSM: frame header 0xD0
        if (header[0] == 0xD0)
            return (true, "gsm");

        return (false, "unknown");
    }

    public bool IsValidFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        if (filename.Contains("..")) return false;
        return FilenameRegex().IsMatch(filename);
    }

    public async Task<List<AudioFileInfo>> GetFilesAsync(string directoryPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath)) return [];

        var result = new List<AudioFileInfo>();
        var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".gsm", ".sln", ".ulaw", ".alaw", ".mp3", ".ogg", ".wav49" };

        await Task.Run(() =>
        {
            foreach (var file in new DirectoryInfo(directoryPath).EnumerateFiles())
            {
                if (!audioExtensions.Contains(file.Extension)) continue;

                TimeSpan? duration = null;
                if (file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    duration = TryParseWavDuration(file.FullName);

                result.Add(new AudioFileInfo(file.Name, file.Length, file.LastWriteTimeUtc, duration));
            }
        }, ct);

        return result.OrderByDescending(f => f.LastModified).ToList();
    }

    public FileStream? GetStream(string basePath, string filename)
    {
        if (!IsValidFilename(filename)) return null;

        var fullPath = Path.Combine(basePath, filename);
        var resolved = Path.GetFullPath(fullPath);

        // Ensure resolved path is inside basePath
        if (!resolved.StartsWith(Path.GetFullPath(basePath), StringComparison.Ordinal))
            return null;

        if (!File.Exists(resolved)) return null;

        return new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<(bool Success, string? Error)> ConvertWithSoxAsync(
        string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!IsSoxAvailable())
            return (false, "sox is not installed");

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "sox",
                Arguments = $"\"{inputPath}\" -r 8000 -c 1 -t wav \"{outputPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                SoxFailed(_logger, inputPath, stderr);
                return (false, $"sox conversion failed: {stderr}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            SoxException(_logger, ex, inputPath);
            return (false, $"sox error: {ex.Message}");
        }
    }

    public static string GetContentType(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".wav" or ".wav49" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".gsm" => "audio/gsm",
            _ => "application/octet-stream",
        };
    }

    private static TimeSpan? TryParseWavDuration(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[44];
            if (fs.Read(header) < 44) return null;

            // Verify RIFF + WAVEfmt
            if (header[0] != 'R' || header[8] != 'W') return null;

            var channels = BitConverter.ToInt16(header[22..24]);
            var sampleRate = BitConverter.ToInt32(header[24..28]);
            var bitsPerSample = BitConverter.ToInt16(header[34..36]);

            if (sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0) return null;

            var dataSize = fs.Length - 44;
            var bytesPerSample = bitsPerSample / 8 * channels;
            if (bytesPerSample <= 0) return null;

            var totalSamples = dataSize / bytesPerSample;
            return TimeSpan.FromSeconds((double)totalSamples / sampleRate);
        }
        catch
        {
            return null;
        }
    }

    private static bool CheckSox()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "sox",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9_.\-]*$")]
    private static partial Regex FilenameRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sox conversion failed for {Path}: {StdErr}")]
    private static partial void SoxFailed(ILogger logger, string path, string stdErr);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sox exception for {Path}")]
    private static partial void SoxException(ILogger logger, Exception ex, string path);
}
