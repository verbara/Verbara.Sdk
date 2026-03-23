using Asterisk.Sdk.Ami.Events;
using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;
using FluentAssertions;

namespace PbxAdmin.Tests.Services.Dialplan;

public class DialplanDiscoveryServiceTests
{
    [Fact]
    public void BuildSnapshot_ShouldGroupEventsByContext()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "_2XXX", Priority = 1, Application = "Dial", AppData = "PJSIP/${EXTEN},30", Registrar = "pbx_config" },
            new() { Context = "default", Extension = "_2XXX", Priority = 2, Application = "Hangup", AppData = "", Registrar = "pbx_config" },
            new() { Context = "from-trunk", Extension = "_2XXX", Priority = 1, Application = "Dial", AppData = "PJSIP/${EXTEN},30", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        snapshot.Contexts.Should().HaveCount(2);
        snapshot.Contexts.Should().Contain(c => c.Name == "default");
        snapshot.Contexts.Should().Contain(c => c.Name == "from-trunk");
        var defaultCtx = snapshot.Contexts.First(c => c.Name == "default");
        defaultCtx.Extensions.Should().HaveCount(1);
        defaultCtx.Extensions[0].Priorities.Should().HaveCount(2);
    }

    [Fact]
    public void BuildSnapshot_ShouldDetectSystemContexts()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
            new() { Context = "__func_periodic_hook_context__", Extension = "hook", Priority = 1, Application = "NoOp", Registrar = "func_periodic_hook" },
            new() { Context = "parkedcalls", Extension = "700", Priority = 1, Application = "Park", Registrar = "res_parking" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        snapshot.Contexts.First(c => c.Name == "default").IsSystem.Should().BeFalse();
        snapshot.Contexts.First(c => c.Name == "__func_periodic_hook_context__").IsSystem.Should().BeTrue();
        snapshot.Contexts.First(c => c.Name == "parkedcalls").IsSystem.Should().BeTrue();
    }

    [Fact]
    public void BuildSnapshot_ShouldParseIncludes()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
            new() { Context = "default", IncludeContext = "parkedcalls", Registrar = "pbx_config" },
            new() { Context = "default", IncludeContext = "outbound-routes", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        var ctx = snapshot.Contexts.First(c => c.Name == "default");
        ctx.Includes.Should().Contain("parkedcalls");
        ctx.Includes.Should().Contain("outbound-routes");
        ctx.Includes.Should().HaveCount(2);
    }

    [Fact]
    public void BuildSnapshot_ShouldParseLabels()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "*69", Priority = 1, Application = "NoOp", Registrar = "pbx_config" },
            new() { Context = "default", Extension = "*69", Priority = 6, ExtensionLabel = "nodata", Application = "Playback", AppData = "vm-norecord", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        var ext = snapshot.Contexts[0].Extensions[0];
        ext.Priorities.Should().Contain(p => p.Label == "nodata");
    }

    [Fact]
    public void BuildSnapshot_ShouldHandleEmptyEventList()
    {
        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", []);
        snapshot.Contexts.Should().BeEmpty();
        snapshot.ServerId.Should().Be("s1");
    }

    [Fact]
    public void BuildSnapshot_ShouldSkipEventsWithNullContext()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = null, Extension = "test", Priority = 1, Application = "NoOp", Registrar = "pbx_config" },
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);
        snapshot.Contexts.Should().HaveCount(1);
        snapshot.Contexts[0].Name.Should().Be("default");
    }

    [Fact]
    public void BuildSnapshot_ShouldGroupMultipleExtensionsInSameContext()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Dial", AppData = "PJSIP/100", Registrar = "pbx_config" },
            new() { Context = "default", Extension = "101", Priority = 1, Application = "Dial", AppData = "PJSIP/101", Registrar = "pbx_config" },
            new() { Context = "default", Extension = "101", Priority = 2, Application = "Hangup", AppData = "", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        var ctx = snapshot.Contexts.Should().ContainSingle().Subject;
        ctx.Extensions.Should().HaveCount(2);
        ctx.Extensions.First(e => e.Pattern == "100").Priorities.Should().HaveCount(1);
        ctx.Extensions.First(e => e.Pattern == "101").Priorities.Should().HaveCount(2);
    }

    [Fact]
    public void BuildSnapshot_ShouldSetCreatedByFromRegistrar()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
            new() { Context = "realtime-ctx", Extension = "200", Priority = 1, Application = "Dial", Registrar = "pbx_realtime" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        snapshot.Contexts.First(c => c.Name == "default").CreatedBy.Should().Be("pbx_config");
        snapshot.Contexts.First(c => c.Name == "realtime-ctx").CreatedBy.Should().Be("pbx_realtime");
    }

    [Fact]
    public void BuildSnapshot_IncludeOnlyEvents_ShouldCreateContextWithNoExtensions()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", IncludeContext = "parkedcalls", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        var ctx = snapshot.Contexts.Should().ContainSingle().Subject;
        ctx.Name.Should().Be("default");
        ctx.Extensions.Should().BeEmpty();
        ctx.Includes.Should().ContainSingle().Which.Should().Be("parkedcalls");
    }

    [Fact]
    public void GetUserContexts_ShouldFilterSystemContexts()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
            new() { Context = "parkedcalls", Extension = "700", Priority = 1, Application = "Park", Registrar = "res_parking" },
            new() { Context = "from-trunk", Extension = "_X.", Priority = 1, Application = "Dial", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);
        var userContexts = DialplanDiscoveryService.GetUserContexts(snapshot);

        userContexts.Should().HaveCount(2);
        userContexts.Should().Contain(c => c.Name == "default");
        userContexts.Should().Contain(c => c.Name == "from-trunk");
        userContexts.Should().NotContain(c => c.Name == "parkedcalls");
    }

    [Fact]
    public void ContextExists_ShouldReturnTrue_WhenContextKnown()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        DialplanDiscoveryService.ContextExists(snapshot, "default").Should().BeTrue();
    }

    [Fact]
    public void ContextExists_ShouldReturnFalse_WhenContextUnknown()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "default", Extension = "100", Priority = 1, Application = "Answer", Registrar = "pbx_config" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        DialplanDiscoveryService.ContextExists(snapshot, "nonexistent").Should().BeFalse();
    }

    [Fact]
    public void BuildSnapshot_ShouldDetectKnownSystemContexts()
    {
        var systemNames = new[] { "parkedcalls", "default-hints", "adhoc-conference" };
        var events = systemNames.Select(name =>
            new ListDialplanEvent { Context = name, Extension = "s", Priority = 1, Application = "NoOp", Registrar = "res_parking" }
        ).ToList();

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        foreach (var ctx in snapshot.Contexts)
        {
            ctx.IsSystem.Should().BeTrue($"'{ctx.Name}' should be detected as system context");
        }
    }

    [Fact]
    public void BuildSnapshot_ShouldDetectSystemByNonUserRegistrar()
    {
        var events = new List<ListDialplanEvent>
        {
            new() { Context = "my-custom-ctx", Extension = "s", Priority = 1, Application = "NoOp", Registrar = "res_pjsip" },
        };

        var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);

        snapshot.Contexts[0].IsSystem.Should().BeTrue();
    }

    [Fact]
    public void BuildSnapshot_ShouldNotDetectUserRegistrarAsSystem()
    {
        var registrars = new[] { "pbx_config", "pbx_realtime", "pbx_lua", "pbx_ael" };
        foreach (var reg in registrars)
        {
            var events = new List<ListDialplanEvent>
            {
                new() { Context = $"ctx-{reg}", Extension = "100", Priority = 1, Application = "Answer", Registrar = reg },
            };

            var snapshot = DialplanDiscoveryService.BuildSnapshot("s1", events);
            snapshot.Contexts[0].IsSystem.Should().BeFalse($"registrar '{reg}' should not be system");
        }
    }
}
