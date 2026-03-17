using PbxAdmin.Models;

namespace PbxAdmin.Services.Dialplan;

public sealed record DialplanData(
    List<InboundRouteConfig> InboundRoutes,
    List<OutboundRouteConfig> OutboundRoutes,
    List<TimeConditionConfig> TimeConditions,
    List<IvrMenuConfig>? IvrMenus = null);
