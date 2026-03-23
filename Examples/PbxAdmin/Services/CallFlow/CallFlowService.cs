using System.Collections.Concurrent;
using System.Globalization;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services.CallFlow;

internal static partial class CallFlowServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[CALLFLOW] Graph built: server={ServerId} flows={FlowCount} warnings={WarningCount}")]
    public static partial void GraphBuilt(ILogger logger, string serverId, int flowCount, int warningCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[CALLFLOW] Failed to build graph: server={ServerId}")]
    public static partial void BuildFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[CALLFLOW] Cache invalidated: server={ServerId}")]
    public static partial void CacheInvalidated(ILogger logger, string serverId);
}

/// <summary>
/// Builds a call flow graph from inbound routes, time conditions, IVR menus,
/// queues, extensions, and trunks. The graph is used for visualization,
/// health warnings, and cross-reference lookups.
/// </summary>
public sealed class CallFlowService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly RouteService _routeService;
    private readonly TimeConditionService _tcService;
    private readonly IIvrMenuRepository _ivrRepo;
    private readonly IQueueConfigService _queueService;
    private readonly IExtensionService _extensionService;
    private readonly ITrunkService _trunkService;
    private readonly ILogger<CallFlowService> _logger;

    private readonly ConcurrentDictionary<string, CallFlowGraph> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CallFlowService(
        RouteService routeService,
        TimeConditionService tcService,
        IIvrMenuRepository ivrRepo,
        IQueueConfigService queueService,
        IExtensionService extensionService,
        ITrunkService trunkService,
        ILogger<CallFlowService> logger)
    {
        _routeService = routeService;
        _tcService = tcService;
        _ivrRepo = ivrRepo;
        _queueService = queueService;
        _extensionService = extensionService;
        _trunkService = trunkService;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Builds (or returns cached) call flow graph for a server.</summary>
    public async Task<CallFlowGraph> BuildFlowAsync(string serverId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(serverId, out var cached) &&
            DateTime.UtcNow - cached.BuiltAt < CacheTtl)
        {
            return cached;
        }

        try
        {
            var inbound = await _routeService.GetAllInboundConfigsAsync(serverId, ct);
            var outbound = await _routeService.GetAllOutboundConfigsAsync(serverId, ct);
            var tcs = await LoadTimeConditionsAsync(serverId, ct);
            var overrides = await _tcService.GetOverridesBatchAsync(serverId, ct);
            var menus = await _ivrRepo.GetMenusAsync(serverId, ct);
            var queues = await LoadQueuesAsync(serverId, ct);
            var extensions = await LoadExtensionsAsync(serverId, ct);
            var trunks = await LoadTrunksAsync(serverId, ct);

            var graph = BuildGraph(serverId, inbound, outbound, tcs, overrides, menus, queues, extensions, trunks);
            _cache[serverId] = graph;

            CallFlowServiceLog.GraphBuilt(_logger, serverId, graph.InboundFlows.Count, graph.Warnings.Count);
            return graph;
        }
        catch (Exception ex)
        {
            CallFlowServiceLog.BuildFailed(_logger, ex, serverId);
            return new CallFlowGraph { ServerId = serverId, BuiltAt = DateTime.UtcNow };
        }
    }

    /// <summary>Returns health warnings for a server's call flow.</summary>
    public async Task<List<HealthWarning>> GetHealthWarningsAsync(string serverId, CancellationToken ct = default)
    {
        var graph = await BuildFlowAsync(serverId, ct);
        return graph.Warnings;
    }

    /// <summary>Returns cross-references for a specific entity.</summary>
    public async Task<List<CrossReference>> GetReferencesForAsync(
        string serverId, string entityType, string entityId, CancellationToken ct = default)
    {
        var graph = await BuildFlowAsync(serverId, ct);
        return GetReferencesFor(graph, entityType, entityId);
    }

    /// <summary>
    /// Traces a call through the routing engine, showing each step the call would take.
    /// </summary>
    public async Task<CallFlowTrace> TraceCallAsync(
        string serverId, string number, DateTime time,
        string overrideMode = "Live", CancellationToken ct = default)
    {
        var inbound = await _routeService.GetAllInboundConfigsAsync(serverId, ct);
        var outbound = await _routeService.GetAllOutboundConfigsAsync(serverId, ct);
        var tcs = await LoadTimeConditionsAsync(serverId, ct);
        var overrides = await _tcService.GetOverridesBatchAsync(serverId, ct);
        var menus = await _ivrRepo.GetMenusAsync(serverId, ct);

        return TraceCall(serverId, inbound, outbound, tcs, overrides, menus, number, time, overrideMode);
    }

    /// <summary>Removes cached graph for a server.</summary>
    public void InvalidateCache(string serverId)
    {
        _cache.TryRemove(serverId, out _);
        CallFlowServiceLog.CacheInvalidated(_logger, serverId);
    }

    // -----------------------------------------------------------------------
    // DTOs for collected data
    // -----------------------------------------------------------------------

    internal sealed record QueueInfo(string Name, string Strategy, int MemberCount, int OnlineCount);
    internal sealed record ExtensionInfo(string Number, string? Name, bool IsRegistered, string Technology);
    internal sealed record TrunkInfo(string Name, bool IsRegistered);

    // -----------------------------------------------------------------------
    // Static graph builder (pure function, testable without mocks)
    // -----------------------------------------------------------------------

    internal static CallFlowGraph BuildGraph(
        string serverId,
        List<InboundRouteConfig> inboundRoutes,
        List<OutboundRouteConfig> outboundRoutes,
        List<TimeConditionConfig> timeConditions,
        Dictionary<string, string> tcOverrides,
        List<IvrMenuConfig> ivrMenus,
        List<QueueInfo> queues,
        List<ExtensionInfo> extensions,
        List<TrunkInfo> trunks)
    {
        var tcByName = timeConditions.ToDictionary(tc => tc.Name, StringComparer.OrdinalIgnoreCase);
        var ivrByName = ivrMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var queueByName = queues.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);
        var extByNumber = extensions.ToDictionary(e => e.Number, StringComparer.OrdinalIgnoreCase);
        var trunkByName = trunks.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var warnings = new List<HealthWarning>();
        var flows = new List<DidNode>();

        // Build inbound flow tree
        foreach (var route in inboundRoutes.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            var dest = ResolveDestination(
                serverId, route.DestinationType, route.Destination,
                tcByName, ivrByName, queueByName, extByNumber,
                warnings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (dest is null && !string.IsNullOrEmpty(route.Destination))
            {
                warnings.Add(new HealthWarning
                {
                    Severity = "Error",
                    Category = "BrokenRef",
                    Message = $"Inbound route '{route.Name}' references missing {route.DestinationType} '{route.Destination}'",
                    EntityType = "InboundRoute",
                    EntityId = route.Id.ToString(CultureInfo.InvariantCulture),
                    NavigateUrl = $"/routes/inbound/edit/{route.Id}",
                });
            }

            flows.Add(new DidNode
            {
                EntityType = "InboundRoute",
                EntityId = route.Id.ToString(CultureInfo.InvariantCulture),
                Label = route.Name,
                EditUrl = $"/routes/inbound/edit/{route.Id}",
                DidPattern = route.DidPattern,
                RouteName = route.Name,
                Priority = route.Priority,
                Destination = dest,
            });
        }

        // Health: TC overrides
        foreach (var (tcName, overrideValue) in tcOverrides)
        {
            if (tcByName.TryGetValue(tcName, out var tc))
            {
                warnings.Add(new HealthWarning
                {
                    Severity = "Warning",
                    Category = "Operational",
                    Message = $"Time condition '{tcName}' has an active override ({overrideValue})",
                    EntityType = "TimeCondition",
                    EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                    NavigateUrl = $"/time-conditions/edit/{tc.Id}",
                });
            }
        }

        // Health: empty queues (only those actually used in the graph)
        foreach (var queue in queues.Where(q => q.OnlineCount == 0))
        {
            warnings.Add(new HealthWarning
            {
                Severity = "Warning",
                Category = "Operational",
                Message = $"Queue '{queue.Name}' has 0 online members ({queue.MemberCount} total)",
                EntityType = "Queue",
                EntityId = queue.Name,
                NavigateUrl = $"/queue-config/{serverId}/{queue.Name}",
            });
        }

        // Health: outbound route trunk issues
        foreach (var route in outboundRoutes.Where(r => r.Enabled))
        {
            foreach (var rt in route.Trunks)
            {
                if (trunkByName.TryGetValue(rt.TrunkName, out var trunk) && !trunk.IsRegistered)
                {
                    warnings.Add(new HealthWarning
                    {
                        Severity = "Error",
                        Category = "BrokenRef",
                        Message = $"Outbound route '{route.Name}' uses trunk '{rt.TrunkName}' which is not registered",
                        EntityType = "OutboundRoute",
                        EntityId = route.Id.ToString(CultureInfo.InvariantCulture),
                        NavigateUrl = $"/routes/outbound/edit/{route.Id}",
                    });
                }
                else if (!trunkByName.ContainsKey(rt.TrunkName))
                {
                    warnings.Add(new HealthWarning
                    {
                        Severity = "Error",
                        Category = "BrokenRef",
                        Message = $"Outbound route '{route.Name}' references missing trunk '{rt.TrunkName}'",
                        EntityType = "OutboundRoute",
                        EntityId = route.Id.ToString(CultureInfo.InvariantCulture),
                        NavigateUrl = $"/routes/outbound/edit/{route.Id}",
                    });
                }
            }

            if (route.Trunks.Count == 1)
            {
                warnings.Add(new HealthWarning
                {
                    Severity = "Info",
                    Category = "Coverage",
                    Message = $"Outbound route '{route.Name}' has only a single trunk (no failover)",
                    EntityType = "OutboundRoute",
                    EntityId = route.Id.ToString(CultureInfo.InvariantCulture),
                    NavigateUrl = $"/routes/outbound/edit/{route.Id}",
                });
            }
        }

        return new CallFlowGraph
        {
            ServerId = serverId,
            BuiltAt = DateTime.UtcNow,
            InboundFlows = flows,
            Warnings = warnings,
        };
    }

    // -----------------------------------------------------------------------
    // Asterisk pattern matching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Matches a number against an Asterisk dialplan pattern.
    /// Pattern rules: _X=[0-9], Z=[1-9], N=[2-9], .=1+ chars, !=0+ chars.
    /// Without leading underscore, the pattern is an exact match.
    /// </summary>
    internal static bool MatchesAsteriskPattern(string pattern, string number)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(number))
            return false;

        // No underscore prefix = exact match
        if (!pattern.StartsWith('_'))
            return string.Equals(pattern, number, StringComparison.Ordinal);

        // Skip the underscore
        var patternSpan = pattern.AsSpan(1);
        var numberSpan = number.AsSpan();
        int pi = 0, ni = 0;

        while (pi < patternSpan.Length && ni < numberSpan.Length)
        {
            char pc = patternSpan[pi];
            char nc = numberSpan[ni];

            switch (pc)
            {
                case '.':
                    // Match one or more remaining characters — must consume at least one
                    return ni < numberSpan.Length;

                case '!':
                    // Match zero or more remaining characters
                    return true;

                case 'X':
                    if (nc is < '0' or > '9') return false;
                    break;

                case 'Z':
                    if (nc is < '1' or > '9') return false;
                    break;

                case 'N':
                    if (nc is < '2' or > '9') return false;
                    break;

                default:
                    if (pc != nc) return false;
                    break;
            }

            pi++;
            ni++;
        }

        // Handle trailing wildcards
        if (pi < patternSpan.Length)
        {
            char remaining = patternSpan[pi];
            if (remaining == '!') return true; // ! matches zero remaining
        }

        return pi == patternSpan.Length && ni == numberSpan.Length;
    }

    // -----------------------------------------------------------------------
    // Call trace (pure function, testable without mocks)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Traces a call through inbound/outbound routing, evaluating time conditions
    /// and IVR menus along the way. Returns a step-by-step trace.
    /// </summary>
    internal static CallFlowTrace TraceCall(
        string serverId,
        List<InboundRouteConfig> inboundRoutes,
        List<OutboundRouteConfig> outboundRoutes,
        List<TimeConditionConfig> timeConditions,
        Dictionary<string, string> tcOverrides,
        List<IvrMenuConfig> ivrMenus,
        string number, DateTime time, string overrideMode)
    {
        var tcByName = timeConditions.ToDictionary(tc => tc.Name, StringComparer.OrdinalIgnoreCase);
        var ivrByName = ivrMenus.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var steps = new List<CallFlowTraceStep>();
        int stepNum = 0;

        // 1. Try inbound routes first (ordered by priority)
        var matchedInbound = inboundRoutes
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .FirstOrDefault(r => MatchesAsteriskPattern(r.DidPattern, number));

        if (matchedInbound is not null)
        {
            steps.Add(new CallFlowTraceStep
            {
                StepNumber = ++stepNum,
                Description = $"Inbound route matched: {matchedInbound.Name} (DID {matchedInbound.DidPattern}, priority {matchedInbound.Priority.ToString(CultureInfo.InvariantCulture)})",
                Result = "Matched",
                EntityType = "InboundRoute",
                EntityId = matchedInbound.Id.ToString(CultureInfo.InvariantCulture),
                EditUrl = $"/routes/inbound/edit/{matchedInbound.Id}",
                DialplanLines = [$"exten => {matchedInbound.DidPattern},1,Goto({matchedInbound.DestinationType}-{matchedInbound.Destination},s,1)"],
            });

            TraceDestination(
                serverId, matchedInbound.DestinationType, matchedInbound.Destination,
                tcByName, ivrByName, tcOverrides,
                time, overrideMode, steps, ref stepNum,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            return new CallFlowTrace
            {
                InputNumber = number,
                InputTime = time,
                Direction = "Inbound",
                OverrideMode = overrideMode,
                Steps = steps,
                RouteFound = true,
            };
        }

        // 2. Try outbound routes
        var matchedOutbound = outboundRoutes
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .FirstOrDefault(r => MatchesAsteriskPattern(r.DialPattern, number));

        if (matchedOutbound is not null)
        {
            steps.Add(new CallFlowTraceStep
            {
                StepNumber = ++stepNum,
                Description = $"Outbound route matched: {matchedOutbound.Name} (pattern {matchedOutbound.DialPattern})",
                Result = "Matched",
                EntityType = "OutboundRoute",
                EntityId = matchedOutbound.Id.ToString(CultureInfo.InvariantCulture),
                EditUrl = $"/routes/outbound/edit/{matchedOutbound.Id}",
                DialplanLines = [$"exten => {matchedOutbound.DialPattern},1,NoOp(Outbound route: {matchedOutbound.Name})"],
            });

            // Number manipulation
            if (!string.IsNullOrEmpty(matchedOutbound.Prefix) || !string.IsNullOrEmpty(matchedOutbound.Prepend))
            {
                var transformed = NumberManipulator.Apply(number, matchedOutbound.Prefix, matchedOutbound.Prepend);
                var dialplanLine = $"exten => {matchedOutbound.DialPattern},n,Set(DIAL_NUMBER={transformed})";
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Number manipulation: {number} -> {transformed} (prefix strip '{matchedOutbound.Prefix ?? ""}', prepend '{matchedOutbound.Prepend ?? ""}')",
                    Result = "Applied",
                    EntityType = "OutboundRoute",
                    EntityId = matchedOutbound.Id.ToString(CultureInfo.InvariantCulture),
                    EditUrl = $"/routes/outbound/edit/{matchedOutbound.Id}",
                    DialplanLines = [dialplanLine],
                });
            }

            // Trunks in sequence
            foreach (var trunk in matchedOutbound.Trunks.OrderBy(t => t.Sequence))
            {
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Dial trunk: {trunk.TrunkName} ({trunk.TrunkTechnology}, sequence {trunk.Sequence.ToString(CultureInfo.InvariantCulture)})",
                    Result = "Dial",
                    EntityType = "Trunk",
                    EntityId = trunk.TrunkName,
                    DialplanLines = [$"exten => {matchedOutbound.DialPattern},n,Dial({trunk.TrunkTechnology}/{trunk.TrunkName}/${{DIAL_NUMBER}})"],
                });
            }

            return new CallFlowTrace
            {
                InputNumber = number,
                InputTime = time,
                Direction = "Outbound",
                OverrideMode = overrideMode,
                Steps = steps,
                RouteFound = true,
            };
        }

        // 3. No match
        steps.Add(new CallFlowTraceStep
        {
            StepNumber = ++stepNum,
            Description = $"No route found for {number}",
            Result = "NotFound",
            EntityType = "",
            EntityId = "",
            DialplanLines = [],
        });

        return new CallFlowTrace
        {
            InputNumber = number,
            InputTime = time,
            Direction = "Unknown",
            OverrideMode = overrideMode,
            Steps = steps,
            RouteFound = false,
        };
    }

    private static void TraceDestination(
        string serverId, string destType, string destTarget,
        Dictionary<string, TimeConditionConfig> tcByName,
        Dictionary<string, IvrMenuConfig> ivrByName,
        Dictionary<string, string> tcOverrides,
        DateTime time, string overrideMode,
        List<CallFlowTraceStep> steps, ref int stepNum,
        HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(destType)) return;

        switch (destType)
        {
            case "extension":
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Destination: Extension {destTarget}",
                    Result = "Terminal",
                    EntityType = "Extension",
                    EntityId = destTarget,
                    EditUrl = $"/extensions/edit/{serverId}/{destTarget}",
                    DialplanLines = [$"exten => s,n,Dial(PJSIP/{destTarget})"],
                });
                break;

            case "queue":
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Destination: Queue '{destTarget}'",
                    Result = "Terminal",
                    EntityType = "Queue",
                    EntityId = destTarget,
                    EditUrl = $"/queue-config/{serverId}/{destTarget}",
                    DialplanLines = [$"exten => s,n,Queue({destTarget})"],
                });
                break;

            case "time_condition":
            {
                var visitKey = $"tc:{destTarget}";
                if (!visited.Add(visitKey)) return; // cycle prevention

                if (!tcByName.TryGetValue(destTarget, out var tc))
                {
                    steps.Add(new CallFlowTraceStep
                    {
                        StepNumber = ++stepNum,
                        Description = $"Time condition '{destTarget}' not found",
                        Result = "Error",
                        EntityType = "TimeCondition",
                        EntityId = destTarget,
                        DialplanLines = [],
                    });
                    return;
                }

                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Enter time condition '{tc.Name}'",
                    Result = "Entered",
                    EntityType = "TimeCondition",
                    EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                    EditUrl = $"/time-conditions/edit/{tc.Id}",
                    DialplanLines = [$"Goto(tc-{tc.Name},s,1)"],
                });

                // Evaluate schedule
                bool isOpen;
                string evaluation;

                if (string.Equals(overrideMode, "AllOpen", StringComparison.OrdinalIgnoreCase))
                {
                    isOpen = true;
                    evaluation = "Override mode: AllOpen (forced open)";
                }
                else if (string.Equals(overrideMode, "AllClosed", StringComparison.OrdinalIgnoreCase))
                {
                    isOpen = false;
                    evaluation = "Override mode: AllClosed (forced closed)";
                }
                else if (string.Equals(overrideMode, "Live", StringComparison.OrdinalIgnoreCase) &&
                         tcOverrides.TryGetValue(tc.Name, out var overrideVal))
                {
                    isOpen = string.Equals(overrideVal, "OPEN", StringComparison.OrdinalIgnoreCase);
                    evaluation = $"Live override: {overrideVal} (forced {(isOpen ? "open" : "closed")})";
                }
                else
                {
                    var state = TimeConditionService.EvaluateState(tc.Ranges, tc.Holidays, time);
                    isOpen = state == TimeConditionState.Open;
                    evaluation = isOpen
                        ? $"Schedule matched at {time:ddd HH:mm} — within configured time range"
                        : $"Schedule not matched at {time:ddd HH:mm} — outside configured time ranges";
                }

                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Evaluate schedule: {evaluation}",
                    Evaluation = evaluation,
                    Result = isOpen ? "Matched" : "NotMatched",
                    EntityType = "TimeCondition",
                    EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                    EditUrl = $"/time-conditions/edit/{tc.Id}",
                    DialplanLines = [isOpen
                        ? $"GotoIf($[...matches...]?{tc.MatchDestType}-{tc.MatchDest},s,1)"
                        : $"Goto({tc.NoMatchDestType}-{tc.NoMatchDest},s,1)"],
                });

                // Follow the appropriate branch
                if (isOpen)
                {
                    TraceDestination(serverId, tc.MatchDestType, tc.MatchDest,
                        tcByName, ivrByName, tcOverrides,
                        time, overrideMode, steps, ref stepNum, visited);
                }
                else
                {
                    TraceDestination(serverId, tc.NoMatchDestType, tc.NoMatchDest,
                        tcByName, ivrByName, tcOverrides,
                        time, overrideMode, steps, ref stepNum, visited);
                }

                visited.Remove(visitKey);
                break;
            }

            case "ivr":
            {
                var ivrVisitKey = $"ivr:{destTarget}";
                if (!visited.Add(ivrVisitKey)) return;

                if (!ivrByName.TryGetValue(destTarget, out var ivr))
                {
                    steps.Add(new CallFlowTraceStep
                    {
                        StepNumber = ++stepNum,
                        Description = $"IVR menu '{destTarget}' not found",
                        Result = "Error",
                        EntityType = "IvrMenu",
                        EntityId = destTarget,
                        DialplanLines = [],
                    });
                    return;
                }

                var optionsSummary = string.Join(", ", ivr.Items.Select(i =>
                    $"{i.Digit}: {i.Label ?? $"{i.DestType} {i.DestTarget}"}"));

                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Enter IVR menu '{ivr.Name}'",
                    Evaluation = $"Options: {optionsSummary}",
                    Result = "Entered",
                    EntityType = "IvrMenu",
                    EntityId = ivr.Id.ToString(CultureInfo.InvariantCulture),
                    EditUrl = $"/ivr-menus/edit/{ivr.Id}",
                    DialplanLines = [$"Goto(ivr-{ivr.Name},s,1)"],
                });

                visited.Remove(ivrVisitKey);
                break;
            }

            case "voicemail":
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = $"Destination: Voicemail {destTarget}",
                    Result = "Terminal",
                    EntityType = "Voicemail",
                    EntityId = destTarget,
                    DialplanLines = [$"exten => s,n,VoiceMail({destTarget}@default)"],
                });
                break;

            case "hangup":
                steps.Add(new CallFlowTraceStep
                {
                    StepNumber = ++stepNum,
                    Description = "Destination: Hangup",
                    Result = "Terminal",
                    EntityType = "Hangup",
                    EntityId = "hangup",
                    DialplanLines = ["exten => s,n,Hangup()"],
                });
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Cross-reference walker
    // -----------------------------------------------------------------------

    /// <summary>Walks the graph to find all entities that reference the target.</summary>
    internal static List<CrossReference> GetReferencesFor(CallFlowGraph graph, string entityType, string entityId)
    {
        var refs = new List<CrossReference>();

        foreach (var did in graph.InboundFlows)
        {
            WalkNodeForReferences(did, did.Destination, entityType, entityId, refs);
        }

        return refs;
    }

    private static void WalkNodeForReferences(
        DidNode root, CallFlowNode? node, string targetType, string targetId,
        List<CrossReference> refs)
    {
        if (node is null) return;

        switch (node)
        {
            case TimeConditionNode tc:
                if (MatchesTarget(tc.OpenBranch, targetType, targetId) ||
                    MatchesTarget(tc.ClosedBranch, targetType, targetId))
                {
                    refs.Add(new CrossReference
                    {
                        SourceType = "TimeCondition",
                        SourceId = tc.EntityId,
                        SourceLabel = tc.Label,
                        Relationship = "routes to",
                    });
                }
                WalkNodeForReferences(root, tc.OpenBranch, targetType, targetId, refs);
                WalkNodeForReferences(root, tc.ClosedBranch, targetType, targetId, refs);
                break;

            case IvrNode ivr:
                foreach (var option in ivr.Options)
                {
                    if (MatchesTarget(option.Destination, targetType, targetId))
                    {
                        refs.Add(new CrossReference
                        {
                            SourceType = "IvrMenu",
                            SourceId = ivr.EntityId,
                            SourceLabel = ivr.Label,
                            Relationship = $"digit {option.Digit} routes to",
                        });
                    }
                    WalkNodeForReferences(root, option.Destination, targetType, targetId, refs);
                }
                break;
        }

        // Check if the DID itself directly targets
        if (node == root.Destination && MatchesTarget(node, targetType, targetId))
        {
            refs.Add(new CrossReference
            {
                SourceType = "InboundRoute",
                SourceId = root.EntityId,
                SourceLabel = root.RouteName,
                Relationship = "routes to",
            });
        }
    }

    private static bool MatchesTarget(CallFlowNode? node, string targetType, string targetId)
    {
        if (node is null) return false;
        return string.Equals(node.EntityType, targetType, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(node.EntityId, targetId, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Destination resolver (recursive)
    // -----------------------------------------------------------------------

    private static CallFlowNode? ResolveDestination(
        string serverId, string destType, string destTarget,
        Dictionary<string, TimeConditionConfig> tcByName,
        Dictionary<string, IvrMenuConfig> ivrByName,
        Dictionary<string, QueueInfo> queueByName,
        Dictionary<string, ExtensionInfo> extByNumber,
        List<HealthWarning> warnings,
        HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(destType)) return null;

        switch (destType)
        {
            case "extension":
                if (extByNumber.TryGetValue(destTarget, out var ext))
                {
                    return new ExtensionNode
                    {
                        EntityType = "Extension",
                        EntityId = ext.Number,
                        Label = ext.Name ?? ext.Number,
                        EditUrl = $"/extensions/edit/{serverId}/{ext.Number}",
                        Number = ext.Number,
                        DisplayName = ext.Name,
                        IsRegistered = ext.IsRegistered,
                        Technology = ext.Technology,
                    };
                }
                // Extension not in list — could be valid but unregistered; create node anyway
                return new ExtensionNode
                {
                    EntityType = "Extension",
                    EntityId = destTarget,
                    Label = destTarget,
                    EditUrl = $"/extensions/edit/{serverId}/{destTarget}",
                    Number = destTarget,
                    IsRegistered = false,
                    Technology = "Unknown",
                };

            case "queue":
                if (queueByName.TryGetValue(destTarget, out var queue))
                {
                    return new QueueNode
                    {
                        EntityType = "Queue",
                        EntityId = queue.Name,
                        Label = queue.Name,
                        EditUrl = $"/queue-config/{serverId}/{queue.Name}",
                        Strategy = queue.Strategy,
                        MemberCount = queue.MemberCount,
                        OnlineCount = queue.OnlineCount,
                    };
                }
                return null;

            case "time_condition":
                var visitKey = $"tc:{destTarget}";
                if (!visited.Add(visitKey)) return null; // cycle prevention

                if (tcByName.TryGetValue(destTarget, out var tc))
                {
                    var openBranch = ResolveDestination(
                        serverId, tc.MatchDestType, tc.MatchDest,
                        tcByName, ivrByName, queueByName, extByNumber, warnings, visited);

                    if (openBranch is null && !string.IsNullOrEmpty(tc.MatchDest))
                    {
                        warnings.Add(new HealthWarning
                        {
                            Severity = "Error",
                            Category = "BrokenRef",
                            Message = $"Time condition '{tc.Name}' open branch references missing {tc.MatchDestType} '{tc.MatchDest}'",
                            EntityType = "TimeCondition",
                            EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                            NavigateUrl = $"/time-conditions/edit/{tc.Id}",
                        });
                    }

                    var closedBranch = ResolveDestination(
                        serverId, tc.NoMatchDestType, tc.NoMatchDest,
                        tcByName, ivrByName, queueByName, extByNumber, warnings, visited);

                    if (closedBranch is null && !string.IsNullOrEmpty(tc.NoMatchDest))
                    {
                        warnings.Add(new HealthWarning
                        {
                            Severity = "Error",
                            Category = "BrokenRef",
                            Message = $"Time condition '{tc.Name}' closed branch references missing {tc.NoMatchDestType} '{tc.NoMatchDest}'",
                            EntityType = "TimeCondition",
                            EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                            NavigateUrl = $"/time-conditions/edit/{tc.Id}",
                        });
                    }

                    visited.Remove(visitKey);
                    return new TimeConditionNode
                    {
                        EntityType = "TimeCondition",
                        EntityId = tc.Id.ToString(CultureInfo.InvariantCulture),
                        Label = tc.Name,
                        EditUrl = $"/time-conditions/edit/{tc.Id}",
                        ScheduleSummary = $"{tc.Ranges.Count.ToString(CultureInfo.InvariantCulture)} range(s)",
                        CurrentState = tc.Enabled ? "Active" : "Disabled",
                        OpenBranch = openBranch,
                        ClosedBranch = closedBranch,
                    };
                }
                return null;

            case "ivr":
                var ivrVisitKey = $"ivr:{destTarget}";
                if (!visited.Add(ivrVisitKey)) return null; // cycle prevention

                if (ivrByName.TryGetValue(destTarget, out var ivr))
                {
                    var options = new List<IvrOptionNode>();
                    foreach (var item in ivr.Items)
                    {
                        var optDest = ResolveDestination(
                            serverId, item.DestType, item.DestTarget,
                            tcByName, ivrByName, queueByName, extByNumber, warnings, visited);

                        if (optDest is null && item.DestType != "hangup" && !string.IsNullOrEmpty(item.DestTarget))
                        {
                            warnings.Add(new HealthWarning
                            {
                                Severity = "Error",
                                Category = "BrokenRef",
                                Message = $"IVR '{ivr.Name}' digit {item.Digit} references missing {item.DestType} '{item.DestTarget}'",
                                EntityType = "IvrMenu",
                                EntityId = ivr.Id.ToString(CultureInfo.InvariantCulture),
                                NavigateUrl = $"/ivr-menus/edit/{ivr.Id}",
                            });
                        }

                        options.Add(new IvrOptionNode
                        {
                            Digit = item.Digit,
                            OptionLabel = item.Label ?? $"{item.DestType}: {item.DestTarget}",
                            Destination = optDest,
                        });
                    }

                    visited.Remove(ivrVisitKey);
                    return new IvrNode
                    {
                        EntityType = "IvrMenu",
                        EntityId = ivr.Id.ToString(CultureInfo.InvariantCulture),
                        Label = ivr.Name,
                        EditUrl = $"/ivr-menus/edit/{ivr.Id}",
                        Greeting = ivr.Greeting,
                        Timeout = ivr.Timeout,
                        Options = options,
                    };
                }
                return null;

            case "voicemail":
                return new VoicemailNode
                {
                    EntityType = "Voicemail",
                    EntityId = destTarget,
                    Label = $"VM: {destTarget}",
                    Extension = destTarget,
                };

            case "hangup":
                return new HangupNode
                {
                    EntityType = "Hangup",
                    EntityId = "hangup",
                    Label = "Hangup",
                };

            default:
                return null;
        }
    }

    // -----------------------------------------------------------------------
    // Data loaders
    // -----------------------------------------------------------------------

    private async Task<List<TimeConditionConfig>> LoadTimeConditionsAsync(string serverId, CancellationToken ct)
    {
        var vms = await _tcService.GetTimeConditionsAsync(serverId, ct);
        // GetTimeConditionsAsync returns view models; we need configs.
        // Re-fetch from the same source that TC service uses.
        var configs = new List<TimeConditionConfig>();
        foreach (var vm in vms)
        {
            var tc = await _tcService.GetTimeConditionAsync(vm.Id, ct);
            if (tc is not null) configs.Add(tc);
        }
        return configs;
    }

    private async Task<List<QueueInfo>> LoadQueuesAsync(string serverId, CancellationToken ct)
    {
        var dtos = await _queueService.GetQueuesAsync(serverId, ct);
        var result = new List<QueueInfo>(dtos.Count);
        foreach (var dto in dtos)
        {
            var members = await _queueService.GetMembersAsync(dto.Id, ct);
            result.Add(new QueueInfo(dto.Name, dto.Strategy, members.Count, members.Count));
        }
        return result;
    }

    private async Task<List<ExtensionInfo>> LoadExtensionsAsync(string serverId, CancellationToken ct)
    {
        var vms = await _extensionService.GetExtensionsAsync(serverId, ct);
        return vms.Select(vm => new ExtensionInfo(
            vm.Extension,
            vm.Name,
            vm.Status == ExtensionStatus.Registered,
            vm.Technology.ToString()
        )).ToList();
    }

    private async Task<List<TrunkInfo>> LoadTrunksAsync(string serverId, CancellationToken ct)
    {
        var vms = await _trunkService.GetTrunksAsync(serverId, ct);
        return vms.Select(vm => new TrunkInfo(
            vm.Name,
            vm.Status == TrunkStatus.Registered
        )).ToList();
    }
}
