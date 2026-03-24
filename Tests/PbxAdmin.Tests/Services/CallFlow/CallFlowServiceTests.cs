using FluentAssertions;
using PbxAdmin.Models;
using PbxAdmin.Services.CallFlow;

namespace PbxAdmin.Tests.Services.CallFlow;

public class CallFlowServiceTests
{
    private const string ServerId = "server1";

    // -----------------------------------------------------------------------
    // Graph building
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFlow_ShouldCreateDidNode_ForInboundRoute()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Main Line",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "1001", Priority = 100, Enabled = true,
            },
        };

        var extensions = new List<CallFlowService.ExtensionInfo>
        {
            new("1001", "John Doe", true, "PJSIP"),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], extensions, []);

        graph.InboundFlows.Should().HaveCount(1);
        var did = graph.InboundFlows[0];
        did.DidPattern.Should().Be("5551234");
        did.RouteName.Should().Be("Main Line");
        did.Priority.Should().Be(100);
        did.Destination.Should().BeOfType<ExtensionNode>();
        var ext = (ExtensionNode)did.Destination!;
        ext.Number.Should().Be("1001");
        ext.DisplayName.Should().Be("John Doe");
        ext.IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void BuildFlow_ShouldCreateTcNode_WhenDestIsTimeCondition()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Hours Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "business-hours", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "business-hours",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "extension", NoMatchDest = "1001",
                Enabled = true,
            },
        };

        var queues = new List<CallFlowService.QueueInfo>
        {
            new("sales", "ringall", 3, 2),
        };

        var extensions = new List<CallFlowService.ExtensionInfo>
        {
            new("1001", "Receptionist", true, "PJSIP"),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], queues, extensions, []);

        graph.InboundFlows.Should().HaveCount(1);
        var did = graph.InboundFlows[0];
        did.Destination.Should().BeOfType<TimeConditionNode>();
        var tc = (TimeConditionNode)did.Destination!;
        tc.Label.Should().Be("business-hours");
        tc.OpenBranch.Should().BeOfType<QueueNode>();
        tc.ClosedBranch.Should().BeOfType<ExtensionNode>();
        ((QueueNode)tc.OpenBranch!).Label.Should().Be("sales");
        ((ExtensionNode)tc.ClosedBranch!).Number.Should().Be("1001");
    }

    [Fact]
    public void BuildFlow_ShouldCreateIvrNode_WhenDestIsIvr()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "IVR Route",
                DidPattern = "5551234", DestinationType = "ivr",
                Destination = "main", Priority = 100, Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "main", Label = "Main Menu",
                Greeting = "welcome", Timeout = 5, MaxRetries = 3,
                Items =
                [
                    new() { Digit = "1", DestType = "queue", DestTarget = "sales" },
                    new() { Digit = "2", DestType = "extension", DestTarget = "1001" },
                ],
            },
        };

        var queues = new List<CallFlowService.QueueInfo> { new("sales", "ringall", 2, 2) };
        var extensions = new List<CallFlowService.ExtensionInfo> { new("1001", "Help", true, "PJSIP") };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            menus, queues, extensions, []);

        var did = graph.InboundFlows[0];
        did.Destination.Should().BeOfType<IvrNode>();
        var ivr = (IvrNode)did.Destination!;
        ivr.Label.Should().Be("main");
        ivr.Greeting.Should().Be("welcome");
        ivr.Options.Should().HaveCount(2);
        ivr.Options[0].Digit.Should().Be("1");
        ivr.Options[0].Destination.Should().BeOfType<QueueNode>();
        ivr.Options[1].Digit.Should().Be("2");
        ivr.Options[1].Destination.Should().BeOfType<ExtensionNode>();
    }

    [Fact]
    public void BuildFlow_ShouldHandleMissingDestination_Gracefully()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Broken Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "nonexistent", Priority = 100, Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        graph.InboundFlows.Should().HaveCount(1);
        graph.InboundFlows[0].Destination.Should().BeNull();
    }

    [Fact]
    public void BuildFlow_ShouldSetEditUrls()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 5, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "biz-hours", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "biz-hours",
                MatchDestType = "queue", MatchDest = "support",
                NoMatchDestType = "extension", NoMatchDest = "2001",
                Enabled = true,
            },
        };

        var queues = new List<CallFlowService.QueueInfo> { new("support", "ringall", 1, 1) };
        var extensions = new List<CallFlowService.ExtensionInfo> { new("2001", "Admin", true, "PJSIP") };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], queues, extensions, []);

        var did = graph.InboundFlows[0];
        did.EditUrl.Should().Be("/routes/inbound/edit/5");
        var tc = (TimeConditionNode)did.Destination!;
        tc.EditUrl.Should().Be("/time-conditions/edit/10");
        ((QueueNode)tc.OpenBranch!).EditUrl.Should().Be($"/queue-config/{ServerId}/support");
        ((ExtensionNode)tc.ClosedBranch!).EditUrl.Should().Be($"/extensions/edit/{ServerId}/2001");
    }

    [Fact]
    public void BuildFlow_ShouldOrderByPriority()
    {
        var routes = new List<InboundRouteConfig>
        {
            new() { Id = 1, ServerId = ServerId, Name = "C", DidPattern = "300", DestinationType = "hangup", Destination = "", Priority = 300, Enabled = true },
            new() { Id = 2, ServerId = ServerId, Name = "A", DidPattern = "100", DestinationType = "hangup", Destination = "", Priority = 100, Enabled = true },
            new() { Id = 3, ServerId = ServerId, Name = "B", DidPattern = "200", DestinationType = "hangup", Destination = "", Priority = 200, Enabled = true },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        graph.InboundFlows.Should().HaveCount(3);
        graph.InboundFlows[0].Priority.Should().Be(100);
        graph.InboundFlows[1].Priority.Should().Be(200);
        graph.InboundFlows[2].Priority.Should().Be(300);
    }

    // -----------------------------------------------------------------------
    // Health warnings
    // -----------------------------------------------------------------------

    [Fact]
    public void Health_ShouldWarnBrokenRef_WhenRouteTcNotFound()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Missing TC",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "old-hours", Priority = 100, Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Error" && w.Category == "BrokenRef" &&
            w.Message.Contains("old-hours"));
    }

    [Fact]
    public void Health_ShouldWarnBrokenRef_WhenTcDestNotFound()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "biz", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "biz",
                MatchDestType = "queue", MatchDest = "deleted",
                NoMatchDestType = "extension", NoMatchDest = "9999",
                Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], [], [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Error" && w.Category == "BrokenRef" &&
            w.EntityType == "TimeCondition");
    }

    [Fact]
    public void Health_ShouldWarnTcOverride()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "office", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "office",
                MatchDestType = "hangup", MatchDest = "",
                NoMatchDestType = "hangup", NoMatchDest = "",
                Enabled = true,
            },
        };

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["office"] = "OPEN",
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, overrides,
            [], [], [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Operational" &&
            w.Message.Contains("override"));
    }

    [Fact]
    public void Health_ShouldWarnEmptyQueue()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "queue",
                Destination = "support", Priority = 100, Enabled = true,
            },
        };

        var queues = new List<CallFlowService.QueueInfo>
        {
            new("support", "ringall", 3, 0),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], queues, [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Operational" &&
            w.Message.Contains("support") && w.Message.Contains("online"));
    }

    [Fact]
    public void Health_ShouldWarnTrunkDown_WhenUsedByOutbound()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "International",
                DialPattern = "_00X.", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trunks = new List<CallFlowService.TrunkInfo>
        {
            new("carrier-a", false),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], outbound, [], [],
            [], [], [], trunks);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Error" && w.Category == "BrokenRef" &&
            w.Message.Contains("carrier-a"));
    }

    [Fact]
    public void Health_ShouldWarnSingleTrunkRoute()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Local",
                DialPattern = "_NXXNXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trunks = new List<CallFlowService.TrunkInfo>
        {
            new("carrier-a", true),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], outbound, [], [],
            [], [], [], trunks);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Info" && w.Category == "Coverage" &&
            w.Message.Contains("single trunk"));
    }

    // -----------------------------------------------------------------------
    // Health P2 warnings — overlapping patterns
    // -----------------------------------------------------------------------

    [Fact]
    public void Health_ShouldWarnOverlappingPatterns_WhenSamePriority()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Local",
                DialPattern = "_NXXNXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
            new()
            {
                Id = 2, ServerId = ServerId, Name = "10-digit alt",
                DialPattern = "_NXXXXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-b", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trunks = new List<CallFlowService.TrunkInfo>
        {
            new("carrier-a", true),
            new("carrier-b", true),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], outbound, [], [],
            [], [], [], trunks);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Configuration" &&
            w.Message.Contains("same priority"));
    }

    [Fact]
    public void Health_ShouldInfoOverlappingPatterns_WhenDifferentPriority()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Local",
                DialPattern = "_NXXNXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
            new()
            {
                Id = 2, ServerId = ServerId, Name = "10-digit alt",
                DialPattern = "_NXXXXXXXXX", Priority = 200, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-b", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trunks = new List<CallFlowService.TrunkInfo>
        {
            new("carrier-a", true),
            new("carrier-b", true),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], outbound, [], [],
            [], [], [], trunks);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Info" && w.Category == "Configuration" &&
            w.Message.Contains("priority determines"));
    }

    [Fact]
    public void Health_ShouldNotWarn_WhenPatternsDoNotOverlap()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Local",
                DialPattern = "_NXXNXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
            new()
            {
                Id = 2, ServerId = ServerId, Name = "International",
                DialPattern = "_00X.", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-b", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trunks = new List<CallFlowService.TrunkInfo>
        {
            new("carrier-a", true),
            new("carrier-b", true),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], outbound, [], [],
            [], [], [], trunks);

        graph.Warnings.Should().NotContain(w =>
            w.Category == "Configuration" && w.Message.Contains("overlap"));
    }

    // -----------------------------------------------------------------------
    // Health P2 warnings — IVR loops
    // -----------------------------------------------------------------------

    [Fact]
    public void Health_ShouldWarnIvrSelfLoop()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "IVR Route",
                DidPattern = "5551234", DestinationType = "ivr",
                Destination = "main", Priority = 100, Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "main", Label = "Main Menu",
                Items =
                [
                    new() { Digit = "1", DestType = "extension", DestTarget = "1001" },
                    new() { Digit = "9", DestType = "ivr", DestTarget = "main" },
                ],
            },
        };

        var extensions = new List<CallFlowService.ExtensionInfo> { new("1001", "Help", true, "PJSIP") };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            menus, [], extensions, []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Configuration" &&
            w.Message.Contains("main") && w.Message.Contains("loops"));
    }

    [Fact]
    public void Health_ShouldWarnIvrIndirectLoop()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "IVR Route",
                DidPattern = "5551234", DestinationType = "ivr",
                Destination = "a", Priority = 100, Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "a", Label = "Menu A",
                Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "b" }],
            },
            new()
            {
                Id = 21, ServerId = ServerId, Name = "b", Label = "Menu B",
                Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "a" }],
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            menus, [], [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Configuration" &&
            w.Message.Contains("loop"));
    }

    [Fact]
    public void Health_ShouldNotWarnIvr_WhenNoLoop()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "IVR Route",
                DidPattern = "5551234", DestinationType = "ivr",
                Destination = "main", Priority = 100, Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "main", Label = "Main Menu",
                Items = [new() { Digit = "1", DestType = "ivr", DestTarget = "sub" }],
            },
            new()
            {
                Id = 21, ServerId = ServerId, Name = "sub", Label = "Sub Menu",
                Items = [new() { Digit = "1", DestType = "extension", DestTarget = "1001" }],
            },
        };

        var extensions = new List<CallFlowService.ExtensionInfo> { new("1001", "Help", true, "PJSIP") };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            menus, [], extensions, []);

        graph.Warnings.Should().NotContain(w =>
            w.Category == "Configuration" && w.Message.Contains("loop"));
    }

    // -----------------------------------------------------------------------
    // Health P2 warnings — TC without ranges
    // -----------------------------------------------------------------------

    [Fact]
    public void Health_ShouldWarnTcWithoutRanges()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "office", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "office",
                MatchDestType = "hangup", MatchDest = "",
                NoMatchDestType = "hangup", NoMatchDest = "",
                Enabled = true,
                Ranges = [],
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], [], [], []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Configuration" &&
            w.Message.Contains("office") && w.Message.Contains("no schedule ranges"));
    }

    [Fact]
    public void Health_ShouldNotWarnTc_WhenRangesExist()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "office", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "office",
                MatchDestType = "hangup", MatchDest = "",
                NoMatchDestType = "hangup", NoMatchDest = "",
                Enabled = true,
                Ranges = [new() { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) }],
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], [], [], []);

        graph.Warnings.Should().NotContain(w =>
            w.Category == "Configuration" && w.Message.Contains("no schedule ranges"));
    }

    // -----------------------------------------------------------------------
    // Health P2 warnings — unregistered extension destination
    // -----------------------------------------------------------------------

    [Fact]
    public void Health_ShouldWarnUnregisteredExtDest()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "2001", Priority = 100, Enabled = true,
            },
        };

        var extensions = new List<CallFlowService.ExtensionInfo>
        {
            new("2001", "Admin", false, "PJSIP"),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], extensions, []);

        graph.Warnings.Should().Contain(w =>
            w.Severity == "Warning" && w.Category == "Operational" &&
            w.Message.Contains("2001") && w.Message.Contains("not registered"));
    }

    [Fact]
    public void Health_ShouldNotWarn_WhenExtRegistered()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "2001", Priority = 100, Enabled = true,
            },
        };

        var extensions = new List<CallFlowService.ExtensionInfo>
        {
            new("2001", "Admin", true, "PJSIP"),
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], extensions, []);

        graph.Warnings.Should().NotContain(w =>
            w.Category == "Operational" && w.Message.Contains("not registered"));
    }

    // -----------------------------------------------------------------------
    // Cross-references
    // -----------------------------------------------------------------------

    [Fact]
    public void GetReferences_ForTc_ShouldReturnRoutesThatUseIt()
    {
        var routes = new List<InboundRouteConfig>
        {
            new() { Id = 1, ServerId = ServerId, Name = "Route A", DidPattern = "100", DestinationType = "time_condition", Destination = "business-hours", Priority = 100, Enabled = true },
            new() { Id = 2, ServerId = ServerId, Name = "Route B", DidPattern = "200", DestinationType = "time_condition", Destination = "business-hours", Priority = 200, Enabled = true },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "business-hours",
                MatchDestType = "hangup", MatchDest = "",
                NoMatchDestType = "hangup", NoMatchDest = "",
                Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            [], [], [], []);

        var refs = CallFlowService.GetReferencesFor(graph, "TimeCondition", "10");

        refs.Should().HaveCount(2);
        refs.Should().OnlyContain(r => r.SourceType == "InboundRoute");
    }

    [Fact]
    public void GetReferences_ForQueue_ShouldReturnTcsAndIvrs()
    {
        var routes = new List<InboundRouteConfig>
        {
            new() { Id = 1, ServerId = ServerId, Name = "TC Route", DidPattern = "100", DestinationType = "time_condition", Destination = "hours", Priority = 100, Enabled = true },
            new() { Id = 2, ServerId = ServerId, Name = "IVR Route", DidPattern = "200", DestinationType = "ivr", Destination = "main", Priority = 200, Enabled = true },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "hours",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "hangup", NoMatchDest = "",
                Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "main", Label = "Main",
                Items = [new() { Digit = "1", DestType = "queue", DestTarget = "sales" }],
            },
        };

        var queues = new List<CallFlowService.QueueInfo> { new("sales", "ringall", 2, 2) };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], tcs, [],
            menus, queues, [], []);

        var refs = CallFlowService.GetReferencesFor(graph, "Queue", "sales");

        refs.Should().HaveCount(2);
        refs.Select(r => r.SourceType).Should().Contain("TimeCondition").And.Contain("IvrMenu");
    }

    [Fact]
    public void GetReferences_ForUnusedEntity_ShouldReturnEmpty()
    {
        var queues = new List<CallFlowService.QueueInfo> { new("empty", "ringall", 0, 0) };

        var graph = CallFlowService.BuildGraph(
            ServerId, [], [], [], [],
            [], queues, [], []);

        var refs = CallFlowService.GetReferencesFor(graph, "Queue", "empty");

        refs.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Additional edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFlow_ShouldSkipDisabledRoutes()
    {
        var routes = new List<InboundRouteConfig>
        {
            new() { Id = 1, ServerId = ServerId, Name = "Disabled", DidPattern = "100", DestinationType = "hangup", Destination = "", Priority = 100, Enabled = false },
            new() { Id = 2, ServerId = ServerId, Name = "Active", DidPattern = "200", DestinationType = "hangup", Destination = "", Priority = 100, Enabled = true },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        graph.InboundFlows.Should().HaveCount(1);
        graph.InboundFlows[0].RouteName.Should().Be("Active");
    }

    [Fact]
    public void BuildFlow_ShouldCreateVoicemailNode()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "VM Route",
                DidPattern = "5551234", DestinationType = "voicemail",
                Destination = "1001", Priority = 100, Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        var did = graph.InboundFlows[0];
        did.Destination.Should().BeOfType<VoicemailNode>();
        ((VoicemailNode)did.Destination!).Extension.Should().Be("1001");
    }

    [Fact]
    public void BuildFlow_ShouldCreateHangupNode()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Hangup Route",
                DidPattern = "5551234", DestinationType = "hangup",
                Destination = "", Priority = 100, Enabled = true,
            },
        };

        var graph = CallFlowService.BuildGraph(
            ServerId, routes, [], [], [],
            [], [], [], []);

        graph.InboundFlows[0].Destination.Should().BeOfType<HangupNode>();
    }
}
