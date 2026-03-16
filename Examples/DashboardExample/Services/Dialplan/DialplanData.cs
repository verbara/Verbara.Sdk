using DashboardExample.Models;

namespace DashboardExample.Services.Dialplan;

public sealed record DialplanData(
    List<InboundRouteConfig> InboundRoutes,
    List<OutboundRouteConfig> OutboundRoutes,
    List<TimeConditionConfig> TimeConditions);
