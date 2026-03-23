using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PbxAdmin.Services;
using PbxAdmin.Services.Dialplan;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Tests.Services;

public class CosContextDiscoveryTests
{
    [Fact]
    public void ParseContexts_ShouldExtractContextNames()
    {
        var output = """
            [ Context 'default' created by 'pbx_config' ]
              '1000' =>        1. Dial(PJSIP/1000,20)     [extensions.conf:2]

            [ Context 'from-trunk' created by 'pbx_config' ]
              '_X.' =>         1. Goto(default,${EXTEN},1) [extensions.conf:5]

            [ Context 'cos-national' created by 'pbx_config' ]
              Include =>       'cos-local'                  [extensions.conf:10]
              Include =>       'outbound-national'           [extensions.conf:11]
            """;

        var contexts = CosContextDiscovery.ParseContexts(output);

        contexts.Should().HaveCount(3);
        contexts.Should().Contain("default");
        contexts.Should().Contain("from-trunk");
        contexts.Should().Contain("cos-national");
    }

    [Fact]
    public void ParseContexts_ShouldReturnEmpty_WhenOutputEmpty()
    {
        var contexts = CosContextDiscovery.ParseContexts("");

        contexts.Should().BeEmpty();
    }

    [Fact]
    public void ParseContexts_ShouldSkipMalformedLines()
    {
        var output = """
            Some random output line
            [ Context 'valid-context' created by 'module' ]
            Not a context line
            [ Broken line
            """;

        var contexts = CosContextDiscovery.ParseContexts(output);

        contexts.Should().ContainSingle().Which.Should().Be("valid-context");
    }

    [Fact]
    public async Task DiscoverContextsAsync_ShouldCallAmi()
    {
        var provider = Substitute.For<IConfigProvider>();
        provider.ExecuteCommandAsync("s1", "dialplan show", Arg.Any<CancellationToken>())
            .Returns("[ Context 'default' created by 'pbx_config' ]");

        var configResolver = Substitute.For<IConfigProviderResolver>();
        configResolver.GetProvider("s1").Returns(provider);

        var cosResolver = Substitute.For<ICosRepositoryResolver>();
        var logger = Substitute.For<ILogger<CosContextDiscovery>>();

        var sut = new CosContextDiscovery(configResolver, cosResolver, logger);
        var result = await sut.DiscoverContextsAsync("s1");

        result.Should().Contain("default");
    }

    [Fact]
    public async Task DiscoverContextsAsync_ShouldReturnEmpty_WhenOutputNull()
    {
        var provider = Substitute.For<IConfigProvider>();
        provider.ExecuteCommandAsync("s1", "dialplan show", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var configResolver = Substitute.For<IConfigProviderResolver>();
        configResolver.GetProvider("s1").Returns(provider);

        var cosResolver = Substitute.For<ICosRepositoryResolver>();
        var logger = Substitute.For<ILogger<CosContextDiscovery>>();

        var sut = new CosContextDiscovery(configResolver, cosResolver, logger);
        var result = await sut.DiscoverContextsAsync("s1");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateContextExistsAsync_ShouldReturnTrue_WhenContextPresent()
    {
        var provider = Substitute.For<IConfigProvider>();
        provider.ExecuteCommandAsync("s1", "dialplan show", Arg.Any<CancellationToken>())
            .Returns("[ Context 'my-ctx' created by 'pbx_config' ]");

        var configResolver = Substitute.For<IConfigProviderResolver>();
        configResolver.GetProvider("s1").Returns(provider);

        var cosResolver = Substitute.For<ICosRepositoryResolver>();
        var logger = Substitute.For<ILogger<CosContextDiscovery>>();

        var sut = new CosContextDiscovery(configResolver, cosResolver, logger);
        var exists = await sut.ValidateContextExistsAsync("s1", "my-ctx");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateContextExistsAsync_ShouldReturnFalse_WhenContextMissing()
    {
        var provider = Substitute.For<IConfigProvider>();
        provider.ExecuteCommandAsync("s1", "dialplan show", Arg.Any<CancellationToken>())
            .Returns("[ Context 'default' created by 'pbx_config' ]");

        var configResolver = Substitute.For<IConfigProviderResolver>();
        configResolver.GetProvider("s1").Returns(provider);

        var cosResolver = Substitute.For<ICosRepositoryResolver>();
        var logger = Substitute.For<ILogger<CosContextDiscovery>>();

        var sut = new CosContextDiscovery(configResolver, cosResolver, logger);
        var exists = await sut.ValidateContextExistsAsync("s1", "nonexistent");

        exists.Should().BeFalse();
    }
}
