using DashboardExample.Services;
using FluentAssertions;

namespace DashboardExample.Tests.Services;

public class IConfigProviderContractTests
{
    [Fact]
    public void PbxConfigManager_ShouldImplementIConfigProvider()
    {
        typeof(PbxConfigManager).Should().Implement<IConfigProvider>();
    }

    [Fact]
    public void DbConfigProvider_ShouldImplementIConfigProvider()
    {
        typeof(DbConfigProvider).Should().Implement<IConfigProvider>();
    }
}
