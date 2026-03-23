using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services.Dialplan;

/// <summary>
/// Generates Asterisk dialplan contexts for Class-of-Service (COS) hierarchy.
/// Produces pattern contexts (outbound-*), COS hierarchy contexts (cos-*),
/// and shared utility contexts (local-extensions, services).
/// </summary>
public sealed class CosDialplanGenerator(ICosRepositoryResolver repoResolver)
{
    /// <summary>
    /// Special exten value used for include directives in <see cref="DialplanLine"/>.
    /// Rendered by providers as <c>include =&gt; {AppData}</c>.
    /// </summary>
    internal const string IncludeExten = "include";

    /// <summary>
    /// Priority value used for include directive lines.
    /// </summary>
    internal const int IncludePriority = 0;

    /// <summary>
    /// Generates all COS-related dialplan contexts for a server.
    /// </summary>
    public async Task<List<DialplanLine>> GenerateContextsAsync(string serverId, CancellationToken ct = default)
    {
        var cosRepo = repoResolver.GetCosRepository(serverId);
        var patternRepo = repoResolver.GetPatternGroupRepository(serverId);

        var levels = await cosRepo.GetLevelsAsync(serverId, ct);
        var groups = await patternRepo.GetGroupsAsync(serverId, ct);

        var lines = new List<DialplanLine>();

        GeneratePatternContexts(groups, levels, lines);
        GenerateCosHierarchy(levels, lines);
        GenerateSharedContexts(lines);

        return lines;
    }

    /// <summary>
    /// Generates outbound-* pattern contexts from pattern groups.
    /// Groups whose name contains "premium" or "mobile" (case-insensitive)
    /// get AstDB-gated dialplan logic; all others get simple Dial.
    /// </summary>
    private static void GeneratePatternContexts(
        List<CosPatternGroup> groups, List<CosLevel> levels, List<DialplanLine> lines)
    {
        foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var contextName = $"outbound-{group.Name.ToLowerInvariant()}";
            var isGated = IsAstDbGated(group.Name);

            foreach (var pattern in group.Patterns)
            {
                if (isGated)
                    GenerateGatedPattern(contextName, pattern, group.Name, lines);
                else
                    GenerateSimplePattern(contextName, pattern, lines);
            }
        }
    }

    /// <summary>
    /// Generates a simple Dial pattern (not AstDB-gated).
    /// <code>
    /// exten => _pattern,1,Dial(PJSIP/${EXTEN}@pstn-trunk,30)
    ///  same => n,Hangup()
    /// </code>
    /// </summary>
    private static void GenerateSimplePattern(string context, string pattern, List<DialplanLine> lines)
    {
        lines.Add(new DialplanLine(context, pattern, 1, "Dial", "PJSIP/${EXTEN}@pstn-trunk,30"));
        lines.Add(new DialplanLine(context, pattern, 2, "Hangup", ""));
    }

    /// <summary>
    /// Generates an AstDB-gated pattern for premium/mobile contexts.
    /// Fail-closed: missing or empty key blocks the call.
    /// <code>
    /// exten => _pattern,1,Set(ALLOWED=${DB(COS_PREMIUM/${CALLERID(num)})})
    ///  same => n,GotoIf($["${ALLOWED}"="1"]?allow:block)
    ///  same => n(allow),Dial(PJSIP/${EXTEN}@pstn-trunk,30)
    ///  same => n(block),Playback(ss-noservice)
    ///  same => n,Hangup()
    /// </code>
    /// </summary>
    private static void GenerateGatedPattern(
        string context, string pattern, string groupName, List<DialplanLine> lines)
    {
        var dbFamily = $"COS_{groupName.ToUpperInvariant()}";

        lines.Add(new DialplanLine(context, pattern, 1, "Set",
            $"ALLOWED=${{DB({dbFamily}/${{CALLERID(num)}})}}"));
        lines.Add(new DialplanLine(context, pattern, 2, "GotoIf",
            "$[\"${ALLOWED}\"=\"1\"]?allow:block"));
        lines.Add(new DialplanLine(context, pattern, 3, "Dial",
            "PJSIP/${EXTEN}@pstn-trunk,30"));
        lines.Add(new DialplanLine(context, pattern, 4, "Playback",
            "ss-noservice"));
        lines.Add(new DialplanLine(context, pattern, 5, "Hangup", ""));
    }

    /// <summary>
    /// Generates COS hierarchy contexts (cos-*) with include chains.
    /// Each COS level includes its allowed pattern group contexts and
    /// may chain to lower-level COS contexts based on priority ordering.
    /// </summary>
    private static void GenerateCosHierarchy(List<CosLevel> levels, List<DialplanLine> lines)
    {
        var orderedLevels = levels
            .Where(l => l.Enabled)
            .OrderBy(l => l.Priority)
            .ToList();

        foreach (var level in orderedLevels)
        {
            var context = level.AsteriskContext;

            // Include the lower-priority COS context (hierarchy chain)
            var lowerLevel = orderedLevels
                .Where(l => l.Priority < level.Priority)
                .OrderByDescending(l => l.Priority)
                .FirstOrDefault();

            if (lowerLevel is not null)
                lines.Add(new DialplanLine(context, IncludeExten, IncludePriority,
                    "include", lowerLevel.AsteriskContext));

            // Include pattern contexts for each rule in this level
            foreach (var rule in level.Rules.Where(r =>
                string.Equals(r.Action, "ALLOW", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Sequence))
            {
                var groupName = rule.PatternGroupName?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(groupName))
                    lines.Add(new DialplanLine(context, IncludeExten, IncludePriority,
                        "include", $"outbound-{groupName}"));
            }
        }
    }

    /// <summary>
    /// Generates shared utility contexts: local-extensions and services.
    /// These are included by COS contexts that allow internal dialing.
    /// </summary>
    private static void GenerateSharedContexts(List<DialplanLine> lines)
    {
        // local-extensions: internal extension dialing
        lines.Add(new DialplanLine("local-extensions", "_1XXX", 1, "Dial",
            "PJSIP/${EXTEN},30"));
        lines.Add(new DialplanLine("local-extensions", "_1XXX", 2, "Hangup", ""));

        // services: voicemail, parking, feature codes
        lines.Add(new DialplanLine("services", "_*97", 1, "VoiceMailMain",
            "${CALLERID(num)}@default"));
        lines.Add(new DialplanLine("services", "_*98", 1, "VoiceMailMain",
            "@default"));
        lines.Add(new DialplanLine("services", "700", 1, "Park", ""));
    }

    /// <summary>
    /// Determines if a pattern group should be AstDB-gated based on its name.
    /// Premium and mobile groups are gated; all others use simple Dial.
    /// </summary>
    private static bool IsAstDbGated(string groupName) =>
        groupName.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
        groupName.Contains("mobile", StringComparison.OrdinalIgnoreCase);
}
