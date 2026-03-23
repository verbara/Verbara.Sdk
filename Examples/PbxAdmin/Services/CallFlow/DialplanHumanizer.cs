using System.Globalization;
using System.Text.RegularExpressions;

namespace PbxAdmin.Services.CallFlow;

/// <summary>
/// Translates Asterisk dialplan application calls into human-readable descriptions.
/// </summary>
public static partial class DialplanHumanizer
{
    /// <summary>
    /// Produces a human-readable description for a dialplan application and its arguments.
    /// </summary>
    public static string Humanize(string app, string appData)
    {
        return app switch
        {
            "Goto" => HumanizeGoto(appData),
            "Dial" => HumanizeDial(appData),
            "Queue" => HumanizeQueue(appData),
            "GotoIfTime" => HumanizeGotoIfTime(appData),
            "GotoIf" => HumanizeGotoIf(appData),
            "Set" => HumanizeSet(appData),
            "Hangup" => "Hang up",
            "Answer" => "Answer call",
            "VoiceMail" => HumanizeVoiceMail(appData),
            "Background" => $"Play '{appData}' (wait for input)",
            "WaitExten" => $"Wait {appData}s for digit",
            "Playback" => $"Play '{appData}'",
            _ => FormatFallback(app, appData),
        };
    }

    private static string HumanizeGoto(string appData)
    {
        // Format: context,extension,priority
        var parts = appData.Split(',');
        if (parts.Length < 3)
        {
            return FormatFallback("Goto", appData);
        }

        var context = parts[0];
        var extension = parts[1];

        if (context.StartsWith("tc-", StringComparison.Ordinal))
        {
            var name = context["tc-".Length..];
            return $"Check time condition '{name}'";
        }

        if (context.StartsWith("ivr-", StringComparison.Ordinal))
        {
            var name = context["ivr-".Length..];
            return $"Go to IVR '{name}'";
        }

        if (context.Equals("queues", StringComparison.OrdinalIgnoreCase))
        {
            return $"Send to queue '{extension}'";
        }

        return $"Forward to ext {extension}";
    }

    private static string HumanizeDial(string appData)
    {
        // Format: PJSIP/${EXTEN}@trunk-name,timeout,...
        var trunkMatch = TrunkPattern().Match(appData);
        if (trunkMatch.Success)
        {
            var trunkName = trunkMatch.Groups[1].Value;
            var timeout = trunkMatch.Groups[2].Value;
            return $"Dial via {trunkName} ({timeout}s)";
        }

        return FormatFallback("Dial", appData);
    }

    private static string HumanizeQueue(string appData)
    {
        // Format: name,options,url,announceoverride,timeout
        var parts = appData.Split(',');
        var name = parts[0];

        if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]))
        {
            return $"Queue '{name}' ({parts[4]}s timeout)";
        }

        return $"Queue '{name}'";
    }

    private static string HumanizeGotoIfTime(string appData)
    {
        // Format: timerange,daysofweek,daysofmonth,months?context,exten,priority
        var questionIndex = appData.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex < 0)
        {
            return FormatFallback("GotoIfTime", appData);
        }

        var timePart = appData[..questionIndex];
        var destPart = appData[(questionIndex + 1)..];
        var timeParts = timePart.Split(',');
        if (timeParts.Length < 2)
        {
            return FormatFallback("GotoIfTime", appData);
        }

        var timeRange = timeParts[0];
        var dayOfWeek = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(timeParts[1]);

        // Extract destination label from context (e.g., "tc-bh-open" -> "open")
        var destContext = destPart.Split(',')[0];
        var lastDash = destContext.LastIndexOf('-');
        var label = lastDash >= 0 ? destContext[(lastDash + 1)..] : destContext;

        return $"If {dayOfWeek} {timeRange} \u2192 {label}";
    }

    private static string HumanizeGotoIf(string appData)
    {
        // OVERRIDE pattern
        if (appData.Contains("OVERRIDE", StringComparison.Ordinal))
        {
            var overrideMatch = OverridePattern().Match(appData);
            if (overrideMatch.Success)
            {
                var value = overrideMatch.Groups[1].Value;
                var destPart = appData[(appData.IndexOf('?', StringComparison.Ordinal) + 1)..];
                var destContext = destPart.Split(',')[0];
                var lastDash = destContext.LastIndexOf('-');
                var label = lastDash >= 0 ? destContext[(lastDash + 1)..] : destContext;
                return $"If override={value} \u2192 go to {label}";
            }
        }

        // IVR_RETRIES pattern
        if (appData.Contains("IVR_RETRIES", StringComparison.Ordinal))
        {
            var retriesMatch = RetriesPattern().Match(appData);
            if (retriesMatch.Success)
            {
                var limit = retriesMatch.Groups[1].Value;
                return $"If retries < {limit} \u2192 replay menu";
            }
        }

        return FormatFallback("GotoIf", appData);
    }

    private static string HumanizeSet(string appData)
    {
        // __ROUTE= pattern (with optional leading underscores for inheritance)
        if (appData.Contains("ROUTE=", StringComparison.Ordinal))
        {
            var routeMatch = RoutePattern().Match(appData);
            if (routeMatch.Success)
            {
                return $"Set route: {routeMatch.Groups[1].Value}";
            }
        }

        // IVR_RETRIES increment
        if (appData.StartsWith("IVR_RETRIES=", StringComparison.Ordinal))
        {
            return "Increment retry counter";
        }

        // OUTNUM transform
        if (appData.StartsWith("OUTNUM=", StringComparison.Ordinal))
        {
            return "Transform dialed number";
        }

        return FormatFallback("Set", appData);
    }

    private static string HumanizeVoiceMail(string appData)
    {
        // Format: extension@context,flags
        var atIndex = appData.IndexOf('@', StringComparison.Ordinal);
        if (atIndex < 0)
        {
            return FormatFallback("VoiceMail", appData);
        }

        var extension = appData[..atIndex];
        var rest = appData[(atIndex + 1)..];
        var commaIndex = rest.IndexOf(',', StringComparison.Ordinal);
        var flag = commaIndex >= 0 ? rest[(commaIndex + 1)..] : "";

        var flagDesc = flag switch
        {
            "u" => "unavailable",
            "b" => "busy",
            _ => flag,
        };

        return string.IsNullOrEmpty(flagDesc)
            ? $"Voicemail for {extension}"
            : $"Voicemail for {extension} ({flagDesc})";
    }

    private static string FormatFallback(string app, string appData)
    {
        return string.IsNullOrEmpty(appData) ? app : $"{app}({appData})";
    }

    [GeneratedRegex(@"@(trunk-[\w-]+),(\d+)")]
    private static partial Regex TrunkPattern();

    [GeneratedRegex(@"OVERRIDE[^=]*=""?(\w+)")]
    private static partial Regex OverridePattern();

    [GeneratedRegex(@"IVR_RETRIES\}<(\d+)")]
    private static partial Regex RetriesPattern();

    [GeneratedRegex(@"_*ROUTE=(.+)$")]
    private static partial Regex RoutePattern();
}
