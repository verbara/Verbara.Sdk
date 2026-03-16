using DashboardExample.Models;

namespace DashboardExample.Services.Dialplan;

internal static class DialplanGenerator
{
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static List<DialplanLine> Generate(DialplanData data)
    {
        var lines = new List<DialplanLine>();
        GenerateInboundRoutes(data.InboundRoutes, lines);
        GenerateOutboundRoutes(data.OutboundRoutes, lines);
        GenerateTimeConditions(data.TimeConditions, lines);
        return lines;
    }

    private static void GenerateInboundRoutes(List<InboundRouteConfig> routes, List<DialplanLine> lines)
    {
        foreach (var route in routes.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            var appData = ResolveDestination(route.DestinationType, route.Destination);
            lines.Add(new DialplanLine("from-trunk", route.DidPattern, 1, "Goto", appData));
        }
    }

    private static void GenerateOutboundRoutes(List<OutboundRouteConfig> routes, List<DialplanLine> lines)
    {
        foreach (var route in routes.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            var trunks = route.Trunks.OrderBy(t => t.Sequence).ToList();
            if (trunks.Count == 0) continue;

            int prio = 1;
            var hasPrepend = !string.IsNullOrEmpty(route.Prepend) || !string.IsNullOrEmpty(route.Prefix);

            if (hasPrepend)
            {
                var prefixLen = route.Prefix?.Length ?? 0;
                var prepend = route.Prepend ?? "";
                lines.Add(new DialplanLine("outbound-routes", route.DialPattern, prio++, "Set", $"OUTNUM={prepend}${{EXTEN:{prefixLen}}}"));
            }

            var dialVar = hasPrepend ? "${OUTNUM}" : "${EXTEN}";
            var techPrefix = GetTechPrefix(trunks[0].TrunkTechnology);

            lines.Add(new DialplanLine("outbound-routes", route.DialPattern, prio++, "Dial", $"{techPrefix}{dialVar}@{trunks[0].TrunkName},60"));

            for (int i = 1; i < trunks.Count; i++)
            {
                var tp = GetTechPrefix(trunks[i].TrunkTechnology);
                lines.Add(new DialplanLine("outbound-routes", route.DialPattern, prio++, "ExecIf",
                    $"$[\"${{DIALSTATUS}}\"=\"CHANUNAVAIL\"|\"${{DIALSTATUS}}\"=\"CONGESTION\"]?Dial({tp}{dialVar}@{trunks[i].TrunkName},60)"));
            }

            lines.Add(new DialplanLine("outbound-routes", route.DialPattern, prio, "Hangup", ""));
        }
    }

    private static void GenerateTimeConditions(List<TimeConditionConfig> conditions, List<DialplanLine> lines)
    {
        foreach (var tc in conditions.Where(c => c.Enabled))
        {
            var ctx = $"tc-{tc.Name}";
            int prio = 1;

            // Override check
            lines.Add(new DialplanLine(ctx, "s", prio++, "Set", $"OVERRIDE=${{DB(TC_OVERRIDE/{tc.Name})}}"));
            lines.Add(new DialplanLine(ctx, "s", prio++, "GotoIf", $"$[\"${{OVERRIDE}}\"=\"OPEN\"]?{ctx}-open,s,1"));
            lines.Add(new DialplanLine(ctx, "s", prio++, "GotoIf", $"$[\"${{OVERRIDE}}\"=\"CLOSED\"]?{ctx}-closed,s,1"));

            // Holiday checks
            foreach (var h in tc.Holidays)
            {
                var monthName = MonthName(h.Month);
                lines.Add(new DialplanLine(ctx, "s", prio++, "GotoIfTime", $"*,*,{h.Day},{monthName}?{ctx}-closed,s,1"));
            }

            // Time ranges (one per row)
            foreach (var r in tc.Ranges)
            {
                var dayName = DayNames[(int)r.DayOfWeek];
                var timeRange = $"{r.StartTime:HH:mm}-{r.EndTime:HH:mm}";
                lines.Add(new DialplanLine(ctx, "s", prio++, "GotoIfTime", $"{timeRange},{dayName},*,*?{ctx}-open,s,1"));
            }

            // Default: closed
            lines.Add(new DialplanLine(ctx, "s", prio, "Goto", $"{ctx}-closed,s,1"));

            // Open/Closed contexts
            lines.Add(new DialplanLine($"{ctx}-open", "s", 1, "Goto", ResolveDestination(tc.MatchDestType, tc.MatchDest)));
            lines.Add(new DialplanLine($"{ctx}-closed", "s", 1, "Goto", ResolveDestination(tc.NoMatchDestType, tc.NoMatchDest)));
        }
    }

    internal static string ResolveDestination(string type, string target) => type switch
    {
        "extension" => $"from-internal,{target},1",
        "queue" => $"queues,{target},1",
        "time_condition" => $"tc-{target},s,1",
        _ => $"default,s,1"
    };

    private static string GetTechPrefix(string technology) => technology switch
    {
        "PjSip" or "pjsip" => "PJSIP/",
        "Sip" or "sip" => "SIP/",
        "Iax2" or "iax2" => "IAX2/",
        _ => "PJSIP/"
    };

    private static string MonthName(int month) => month switch
    {
        1 => "jan", 2 => "feb", 3 => "mar", 4 => "apr", 5 => "may", 6 => "jun",
        7 => "jul", 8 => "aug", 9 => "sep", 10 => "oct", 11 => "nov", 12 => "dec",
        _ => "jan"
    };
}
