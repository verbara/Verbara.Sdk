using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.IntegrationTests.Hosting;

public class ConfigurationBindingTests
{
    [Fact]
    public async Task AddAsterisk_ShouldBindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asterisk:Ami:Hostname"] = "pbx.example.com",
                ["Asterisk:Ami:Port"] = "5039",
                ["Asterisk:Ami:Username"] = "admin",
                ["Asterisk:Ami:Password"] = "secret"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsterisk(config);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AmiConnectionOptions>>().Value;

        options.Hostname.Should().Be("pbx.example.com");
        options.Port.Should().Be(5039);
        options.Username.Should().Be("admin");
        options.Password.Should().Be("secret");
    }
}
