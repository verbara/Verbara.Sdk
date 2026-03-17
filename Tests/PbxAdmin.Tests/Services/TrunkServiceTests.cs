using PbxAdmin.Models;
using PbxAdmin.Services;
using FluentAssertions;

namespace PbxAdmin.Tests.Services;

public class TrunkServiceTests
{
    [Fact]
    public void GetConfigFilename_ShouldReturnCorrectFile()
    {
        TrunkService.GetConfigFilename(TrunkTechnology.PjSip).Should().Be("pjsip.conf");
        TrunkService.GetConfigFilename(TrunkTechnology.Sip).Should().Be("sip.conf");
        TrunkService.GetConfigFilename(TrunkTechnology.Iax2).Should().Be("iax.conf");
    }

    [Fact]
    public void GetReloadModule_ShouldReturnCorrectModule()
    {
        TrunkService.GetReloadModule(TrunkTechnology.PjSip).Should().Be("res_pjsip.so");
        TrunkService.GetReloadModule(TrunkTechnology.Sip).Should().Be("chan_sip.so");
        TrunkService.GetReloadModule(TrunkTechnology.Iax2).Should().Be("chan_iax2.so");
    }
}
