using Asterisk.Sdk.Ami.Responses;
using PbxAdmin.Models;
using PbxAdmin.Services;
using PbxAdmin.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace PbxAdmin.Tests.Services;

public class MohServiceTests
{
    private const string ServerId = "srv1";

    private static (MohService Sut, IMohClassRepository Repo, IConfigProviderResolver Provider) CreateService()
    {
        var repo = Substitute.For<IMohClassRepository>();
        var schema = Substitute.For<IRecordingMohSchemaManager>();
        var providerResolver = Substitute.For<IConfigProviderResolver>();
        var audioSvc = new AudioFileService(NullLogger<AudioFileService>.Instance);
        var config = Substitute.For<IConfiguration>();
        var logger = NullLogger<MohService>.Instance;
        var sut = new MohService(repo, schema, providerResolver, audioSvc, config, logger);
        return (sut, repo, providerResolver);
    }

    private static MohClass ValidClass() => new()
    {
        ServerId = ServerId,
        Name = "default",
        Mode = "files",
        Directory = "/var/lib/asterisk/moh/default",
        Sort = "random"
    };

    [Fact]
    public async Task CreateClass_ShouldSucceed_WhenValid()
    {
        var (sut, repo, provider) = CreateService();
        repo.GetByNameAsync(ServerId, "default", Arg.Any<CancellationToken>()).Returns((MohClass?)null);
        repo.InsertAsync(Arg.Any<MohClass>(), Arg.Any<CancellationToken>()).Returns(1);
        // Provide a mock config provider so regeneration doesn't NRE
        var configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetCategoriesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConfigCategory>()));
        provider.GetProvider(ServerId).Returns(configProvider);

        var (success, _) = await sut.CreateClassAsync(ServerId, ValidClass());

        success.Should().BeTrue();
    }

    [Fact]
    public async Task CreateClass_ShouldFail_WhenNameDuplicate()
    {
        var (sut, repo, _) = CreateService();
        repo.GetByNameAsync(ServerId, "default", Arg.Any<CancellationToken>()).Returns(ValidClass());

        var (success, error) = await sut.CreateClassAsync(ServerId, ValidClass());

        success.Should().BeFalse();
        error.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateClass_ShouldFail_WhenEmptyName()
    {
        var (sut, _, _) = CreateService();
        var cls = ValidClass();
        cls.Name = "";

        var (success, error) = await sut.CreateClassAsync(ServerId, cls);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Theory]
    [InlineData("valid-name", true)]
    [InlineData("with_underscore", true)]
    [InlineData("has spaces", false)]
    [InlineData("special!chars", false)]
    public async Task CreateClass_ShouldValidateName(string name, bool expectedValid)
    {
        var (sut, repo, _) = CreateService();
        repo.GetByNameAsync(ServerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((MohClass?)null);
        repo.InsertAsync(Arg.Any<MohClass>(), Arg.Any<CancellationToken>()).Returns(1);

        var cls = ValidClass();
        cls.Name = name;

        var (success, _) = await sut.CreateClassAsync(ServerId, cls);

        success.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData("files", true)]
    [InlineData("mp3", true)]
    [InlineData("custom", true)]
    [InlineData("invalid", false)]
    public async Task CreateClass_ShouldValidateMode(string mode, bool expectedValid)
    {
        var (sut, repo, _) = CreateService();
        repo.GetByNameAsync(ServerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((MohClass?)null);
        repo.InsertAsync(Arg.Any<MohClass>(), Arg.Any<CancellationToken>()).Returns(1);

        var cls = ValidClass();
        cls.Mode = mode;
        // custom mode doesn't require Directory
        if (mode == "custom")
            cls.Directory = "";

        var (success, _) = await sut.CreateClassAsync(ServerId, cls);

        success.Should().Be(expectedValid);
    }

    [Fact]
    public void GenerateMohConf_ShouldProduceCorrectIni()
    {
        var classes = new List<MohClass>
        {
            new() { Name = "default", Mode = "files", Directory = "/var/lib/asterisk/moh/default", Sort = "random" },
            new() { Name = "custom-stream", Mode = "custom", Directory = "", CustomApplication = "/usr/bin/mpg123 -q http://stream" },
        };

        var result = MohService.GenerateMohConf(classes);

        result.Should().Contain("[default]");
        result.Should().Contain("mode=files");
        result.Should().Contain("directory=/var/lib/asterisk/moh/default");
        result.Should().Contain("sort=random");
        result.Should().Contain("[custom-stream]");
        result.Should().Contain("mode=custom");
        result.Should().Contain("application=/usr/bin/mpg123 -q http://stream");
    }

    [Fact]
    public async Task UploadAudio_ShouldFail_WhenInvalidFilename()
    {
        var (sut, repo, _) = CreateService();
        repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ValidClass() with { Id = 1 });

        using var stream = new MemoryStream("RIFF\x00\x00\x00\x00WAVEfmt "u8.ToArray());
        var (success, error) = await sut.UploadAudioAsync(1, "../evil.wav", stream, 100, 20, 200);

        success.Should().BeFalse();
        error.Should().Contain("Invalid filename");
    }

    [Fact]
    public async Task UploadAudio_ShouldFail_WhenFileTooLarge()
    {
        var (sut, repo, _) = CreateService();
        repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ValidClass() with { Id = 1 });

        using var stream = new MemoryStream("RIFF\x00\x00\x00\x00WAVEfmt "u8.ToArray());
        // fileSize exceeds maxFileSizeMb (1MB limit, 2MB file)
        var (success, error) = await sut.UploadAudioAsync(1, "test.wav", stream, 2 * 1024 * 1024, 1, 200);

        success.Should().BeFalse();
        error.Should().Contain("limit");
    }

    [Fact]
    public async Task UploadAudio_ShouldFail_WhenUnknownFormat()
    {
        var (sut, repo, _) = CreateService();
        repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ValidClass() with { Id = 1 });

        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
        var (success, error) = await sut.UploadAudioAsync(1, "test.wav", stream, 100, 20, 200);

        success.Should().BeFalse();
        error.Should().Contain("Unrecognized");
    }
}
