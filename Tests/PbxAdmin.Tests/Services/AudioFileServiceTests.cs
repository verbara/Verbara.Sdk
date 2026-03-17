// Tests/PbxAdmin.Tests/Services/AudioFileServiceTests.cs
using PbxAdmin.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace PbxAdmin.Tests.Services;

public class AudioFileServiceTests
{
    private readonly AudioFileService _sut = new(NullLogger<AudioFileService>.Instance);

    [Fact]
    public void ValidateMagicBytes_ShouldDetectWav_WhenRiffHeader()
    {
        // RIFF....WAVEfmt
        var data = "RIFF\x00\x00\x00\x00WAVEfmt "u8.ToArray();
        using var stream = new MemoryStream(data);

        var (valid, format) = _sut.ValidateMagicBytes(stream);

        valid.Should().BeTrue();
        format.Should().Be("wav");
    }

    [Fact]
    public void ValidateMagicBytes_ShouldDetectMp3_WhenId3Header()
    {
        var data = "ID3\x04\x00\x00\x00\x00\x00\x00"u8.ToArray();
        using var stream = new MemoryStream(data);

        var (valid, format) = _sut.ValidateMagicBytes(stream);

        valid.Should().BeTrue();
        format.Should().Be("mp3");
    }

    [Fact]
    public void ValidateMagicBytes_ShouldDetectMp3_WhenSyncBits()
    {
        var data = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        using var stream = new MemoryStream(data);

        var (valid, format) = _sut.ValidateMagicBytes(stream);

        valid.Should().BeTrue();
        format.Should().Be("mp3");
    }

    [Fact]
    public void ValidateMagicBytes_ShouldDetectOgg_WhenOggSHeader()
    {
        var data = "OggS\x00\x02\x00\x00"u8.ToArray();
        using var stream = new MemoryStream(data);

        var (valid, format) = _sut.ValidateMagicBytes(stream);

        valid.Should().BeTrue();
        format.Should().Be("ogg");
    }

    [Fact]
    public void ValidateMagicBytes_ShouldReject_WhenUnknownFormat()
    {
        var data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        using var stream = new MemoryStream(data);

        var (valid, format) = _sut.ValidateMagicBytes(stream);

        valid.Should().BeFalse();
        format.Should().Be("unknown");
    }

    [Fact]
    public void ValidateMagicBytes_ShouldResetStreamPosition()
    {
        var data = "RIFF\x00\x00\x00\x00WAVEfmt "u8.ToArray();
        using var stream = new MemoryStream(data);

        _sut.ValidateMagicBytes(stream);

        stream.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("good-file.wav", true)]
    [InlineData("file_name-2.mp3", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("file/path.wav", false)]
    [InlineData("file\\path.wav", false)]
    [InlineData("", false)]
    public void IsValidFilename_ShouldValidate(string filename, bool expected)
    {
        _sut.IsValidFilename(filename).Should().Be(expected);
    }

    [Fact]
    public async Task GetFilesAsync_ShouldReturnEmpty_WhenDirectoryMissing()
    {
        var result = await _sut.GetFilesAsync("/nonexistent/path/12345");

        result.Should().BeEmpty();
    }
}
