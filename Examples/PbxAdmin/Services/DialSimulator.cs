using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

/// <summary>
/// Pure in-memory dial simulator that evaluates COS rules against a dialed number.
/// Returns a detailed trace of the evaluation steps.
/// </summary>
public sealed class DialSimulator
{
    private readonly ICosRepositoryResolver _repoResolver;
    private readonly IConfiguration _config;

    public DialSimulator(ICosRepositoryResolver repoResolver, IConfiguration config)
    {
        _repoResolver = repoResolver;
        _config = config;
    }

    /// <summary>
    /// Simulates dialing a number from an extension and returns the evaluation result.
    /// </summary>
    public async Task<DialSimulatorResult> SimulateAsync(
        string serverId, string extension, string dialedNumber, CancellationToken ct = default)
    {
        var trace = new List<string>();
        var flagChecks = new List<DialSimulatorFlagCheck>();

        var cosRepo = _repoResolver.GetCosRepository(serverId);
        var patternRepo = _repoResolver.GetPatternGroupRepository(serverId);

        // ---------------------------------------------------------------
        // Step 1: Resolve COS for extension
        // ---------------------------------------------------------------
        trace.Add($"Resolving COS for extension {extension} on server {serverId}");

        var extOverride = await cosRepo.GetExtensionOverrideAsync(serverId, extension, ct);
        CosLevel? cosLevel = null;
        string? patternOverridesJson = null;

        if (extOverride?.CosLevelId is not null)
        {
            cosLevel = await cosRepo.GetLevelAsync(extOverride.CosLevelId.Value, ct);
            patternOverridesJson = extOverride.PatternOverrides;
            trace.Add($"Found per-extension override: COS level={cosLevel?.Name ?? "?"} (id={extOverride.CosLevelId})");
        }
        else
        {
            var defaultId = GetDefaultCosLevelId(serverId);
            if (defaultId is not null)
            {
                cosLevel = await cosRepo.GetLevelAsync(defaultId.Value, ct);
                trace.Add($"Using system default COS: level={cosLevel?.Name ?? "?"} (id={defaultId})");
            }
        }

        if (cosLevel is null)
        {
            trace.Add("No COS assigned to extension and no system default configured");
            return BuildResult(DialSimulatorVerdict.Error, trace, flagChecks,
                errorMessage: "No COS assigned to this extension and no system default is configured.");
        }

        var resolvedCosName = cosLevel.Name;

        // ---------------------------------------------------------------
        // Step 2: Time override check
        // ---------------------------------------------------------------
        string? timeOverrideApplied = null;
        var timeOverrides = await cosRepo.GetTimeOverridesAsync(cosLevel.Id, ct);

        if (timeOverrides.Count > 0)
        {
            trace.Add($"Checking {timeOverrides.Count} time override(s)");
            var timeWindows = await cosRepo.GetTimeWindowsAsync(serverId, ct);
            var now = DateTimeOffset.UtcNow;
            var currentTime = TimeOnly.FromDateTime(now.DateTime);
            var currentDay = (int)now.DayOfWeek;

            foreach (var to in timeOverrides.OrderBy(t => t.Priority))
            {
                var window = timeWindows.FirstOrDefault(w => w.Id == to.TimeWindowId);
                if (window is null) continue;

                if (window.DayOfWeek == currentDay &&
                    currentTime >= window.StartTime &&
                    currentTime <= window.EndTime)
                {
                    var overrideLevel = await cosRepo.GetLevelAsync(to.OverrideCosLevelId, ct);
                    if (overrideLevel is not null)
                    {
                        trace.Add($"Time override active: window='{window.Name}' -> COS='{overrideLevel.Name}'");
                        cosLevel = overrideLevel;
                        resolvedCosName = overrideLevel.Name;
                        timeOverrideApplied = window.Name;
                        break;
                    }
                }
            }

            if (timeOverrideApplied is null)
                trace.Add("No active time override found");
        }

        // ---------------------------------------------------------------
        // Step 3: Per-extension pattern overrides
        // ---------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(patternOverridesJson))
        {
            trace.Add("Checking per-extension pattern overrides");
            try
            {
                var overrides = JsonSerializer.Deserialize<List<PatternOverrideEntry>>(patternOverridesJson);
                if (overrides is not null)
                {
                    foreach (var po in overrides)
                    {
                        if (MatchAsteriskPattern(po.Pattern, dialedNumber))
                        {
                            var verdict = string.Equals(po.Action, "ALLOW", StringComparison.OrdinalIgnoreCase)
                                ? DialSimulatorVerdict.Allowed
                                : DialSimulatorVerdict.Denied;
                            trace.Add($"Matched extension pattern override: pattern='{po.Pattern}' action={po.Action}");
                            return BuildResult(verdict, trace, flagChecks,
                                matchingPattern: po.Pattern,
                                matchingRuleName: "Per-extension override",
                                resolvedCosName: resolvedCosName,
                                timeOverrideApplied: timeOverrideApplied);
                        }
                    }
                }

                trace.Add("No per-extension pattern override matched");
            }
            catch (JsonException)
            {
                trace.Add("Failed to parse per-extension pattern overrides JSON");
            }
        }

        // ---------------------------------------------------------------
        // Step 4: Evaluate COS rules (ordered by sequence, first match wins)
        // ---------------------------------------------------------------
        trace.Add($"Evaluating {cosLevel.Rules.Count} COS rule(s) for '{cosLevel.Name}'");

        string? matchingRuleName = null;
        string? matchingPattern = null;
        string? matchingPatternGroup = null;
        DialSimulatorVerdict? ruleVerdict = null;

        foreach (var rule in cosLevel.Rules.OrderBy(r => r.Sequence))
        {
            var group = await patternRepo.GetGroupAsync(rule.PatternGroupId, ct);
            if (group is null)
            {
                trace.Add($"  Rule seq={rule.Sequence}: pattern group id={rule.PatternGroupId} not found, skipping");
                continue;
            }

            foreach (var pattern in group.Patterns)
            {
                if (MatchAsteriskPattern(pattern, dialedNumber))
                {
                    matchingRuleName = rule.PatternGroupName ?? group.Name;
                    matchingPattern = pattern;
                    matchingPatternGroup = group.Name;
                    ruleVerdict = string.Equals(rule.Action, "ALLOW", StringComparison.OrdinalIgnoreCase)
                        ? DialSimulatorVerdict.Allowed
                        : DialSimulatorVerdict.Denied;
                    trace.Add($"  Rule seq={rule.Sequence}: matched pattern='{pattern}' in group='{group.Name}' -> {rule.Action}");
                    break;
                }
            }

            if (ruleVerdict is not null) break;
        }

        if (ruleVerdict is null)
        {
            trace.Add("No rule matched -> DENIED (closed by default)");
            return BuildResult(DialSimulatorVerdict.Denied, trace, flagChecks,
                resolvedCosName: resolvedCosName,
                timeOverrideApplied: timeOverrideApplied);
        }

        // ---------------------------------------------------------------
        // Step 5: Flag checks (premium / mobile gates)
        // ---------------------------------------------------------------
        trace.Add("Checking premium/mobile flags");

        var allGroups = await patternRepo.GetGroupsAsync(serverId, ct);
        var finalVerdict = ruleVerdict.Value;

        // Premium check
        var premiumGroups = allGroups.Where(g =>
            g.Name.Contains("Premium", StringComparison.OrdinalIgnoreCase)).ToList();
        var premiumMatches = MatchesAnyPattern(premiumGroups, dialedNumber);
        var premiumCheck = new DialSimulatorFlagCheck(
            "AllowPremium", premiumMatches, cosLevel.AllowPremium,
            premiumMatches && !cosLevel.AllowPremium ? "BLOCKED" : "OK");
        flagChecks.Add(premiumCheck);

        if (premiumMatches && !cosLevel.AllowPremium)
        {
            trace.Add("  AllowPremium=false and number matches premium pattern -> DENIED");
            finalVerdict = DialSimulatorVerdict.Denied;
        }

        // Mobile check
        var mobileGroups = allGroups.Where(g =>
            g.Name.Contains("Mobile", StringComparison.OrdinalIgnoreCase)).ToList();
        var mobileMatches = MatchesAnyPattern(mobileGroups, dialedNumber);
        var mobileCheck = new DialSimulatorFlagCheck(
            "AllowMobile", mobileMatches, cosLevel.AllowMobile,
            mobileMatches && !cosLevel.AllowMobile ? "BLOCKED" : "OK");
        flagChecks.Add(mobileCheck);

        if (mobileMatches && !cosLevel.AllowMobile)
        {
            trace.Add("  AllowMobile=false and number matches mobile pattern -> DENIED");
            finalVerdict = DialSimulatorVerdict.Denied;
        }

        trace.Add($"Final verdict: {finalVerdict}");

        return BuildResult(finalVerdict, trace, flagChecks,
            matchingRuleName: matchingRuleName,
            matchingPattern: matchingPattern,
            matchingPatternGroup: matchingPatternGroup,
            resolvedCosName: resolvedCosName,
            timeOverrideApplied: timeOverrideApplied);
    }

    // -------------------------------------------------------------------
    // Pattern matching: Asterisk dialplan patterns to .NET regex
    // -------------------------------------------------------------------

    /// <summary>
    /// Matches a dialed number against an Asterisk dialplan pattern.
    /// Patterns starting with '_' use Asterisk pattern syntax;
    /// patterns without '_' prefix are exact (literal) matches.
    /// </summary>
    public static bool MatchAsteriskPattern(string pattern, string number)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(number))
            return false;

        if (!pattern.StartsWith('_'))
        {
            // Exact match (no Asterisk pattern prefix)
            return string.Equals(pattern, number, StringComparison.Ordinal);
        }

        // Strip the '_' prefix and convert to regex
        var patternBody = pattern.AsSpan(1);
        var regex = new StringBuilder("^");

        for (var i = 0; i < patternBody.Length; i++)
        {
            var ch = patternBody[i];
            switch (ch)
            {
                case 'X':
                    regex.Append("[0-9]");
                    break;
                case 'Z':
                    regex.Append("[1-9]");
                    break;
                case 'N':
                    regex.Append("[2-9]");
                    break;
                case '[':
                    // Pass through character class until ']'
                    regex.Append('[');
                    i++;
                    while (i < patternBody.Length && patternBody[i] != ']')
                    {
                        regex.Append(patternBody[i]);
                        i++;
                    }

                    regex.Append(']');
                    break;
                case '.':
                    regex.Append(".+"); // one or more of anything
                    break;
                case '!':
                    regex.Append(".*"); // zero or more of anything
                    break;
                case '+':
                    regex.Append(@"\+");
                    break;
                case '*':
                    regex.Append(@"\*");
                    break;
                case '#':
                    regex.Append('#');
                    break;
                default:
                    // Digits and other literal characters
                    regex.Append(ch);
                    break;
            }
        }

        regex.Append('$');
        return Regex.IsMatch(number, regex.ToString());
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private static bool MatchesAnyPattern(List<CosPatternGroup> groups, string number)
    {
        foreach (var group in groups)
        {
            foreach (var pattern in group.Patterns)
            {
                if (MatchAsteriskPattern(pattern, number))
                    return true;
            }
        }

        return false;
    }

    private static DialSimulatorResult BuildResult(
        DialSimulatorVerdict verdict,
        List<string> traceLog,
        List<DialSimulatorFlagCheck> flagChecks,
        string? matchingRuleName = null,
        string? matchingPattern = null,
        string? matchingPatternGroup = null,
        string? resolvedCosName = null,
        string? timeOverrideApplied = null,
        string? errorMessage = null)
    {
        return new DialSimulatorResult(
            Verdict: verdict,
            MatchingRuleName: matchingRuleName,
            MatchingPattern: matchingPattern,
            MatchingPatternGroup: matchingPatternGroup,
            FlagChecks: flagChecks,
            TrunkName: null,
            EffectiveDialString: null,
            ResolvedCosName: resolvedCosName,
            TimeOverrideApplied: timeOverrideApplied,
            ErrorMessage: errorMessage,
            TraceLog: traceLog);
    }

    private int? GetDefaultCosLevelId(string serverId)
    {
        var servers = _config.GetSection("Asterisk:Servers").GetChildren();
        foreach (var section in servers)
        {
            if (string.Equals(section["Id"], serverId, StringComparison.OrdinalIgnoreCase))
            {
                var val = section["DefaultCosLevelId"];
                if (int.TryParse(val, CultureInfo.InvariantCulture, out var id)) return id;
            }
        }

        return null;
    }

    // Used for deserializing per-extension pattern overrides JSON
    private sealed class PatternOverrideEntry
    {
        public string Pattern { get; set; } = "";
        public string Action { get; set; } = "ALLOW";
    }
}
