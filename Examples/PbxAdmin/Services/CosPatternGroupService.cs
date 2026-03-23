using System.Text.RegularExpressions;
using PbxAdmin.Models;
using PbxAdmin.Services.Repositories;

namespace PbxAdmin.Services;

internal static partial class CosPatternGroupLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[COS_PG] GetGroups: server={ServerId} count={Count}")]
    public static partial void GetGroups(ILogger logger, string serverId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS_PG] Created: server={ServerId} name={Name}")]
    public static partial void Created(ILogger logger, string serverId, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS_PG] Updated: server={ServerId} id={Id}")]
    public static partial void Updated(ILogger logger, string serverId, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "[COS_PG] Deleted: server={ServerId} id={Id}")]
    public static partial void Deleted(ILogger logger, string serverId, int id);
}

/// <summary>
/// CRUD service for COS pattern groups with Asterisk dialplan pattern validation.
/// </summary>
public sealed partial class CosPatternGroupService
{
    /// <summary>Maximum allowed length for a single pattern.</summary>
    private const int MaxPatternLength = 40;

    /// <summary>
    /// Matches a valid Asterisk pattern body (after the leading underscore).
    /// Allowed: X, Z, N, digits 0-9, *, #, +, character classes [...], and wildcards . and !
    /// </summary>
    [GeneratedRegex(@"^[XZXN0-9*#+.!\[\]0-9\-]+$")]
    private static partial Regex PatternBodyRegex();

    /// <summary>
    /// Matches an exact-match pattern (no underscore prefix).
    /// Only digits, *, #, and + are valid.
    /// </summary>
    [GeneratedRegex(@"^[0-9*#+]+$")]
    private static partial Regex ExactMatchRegex();

    private readonly ICosRepositoryResolver _repoResolver;
    private readonly ILogger<CosPatternGroupService> _logger;

    public CosPatternGroupService(ICosRepositoryResolver repoResolver, ILogger<CosPatternGroupService> logger)
    {
        _repoResolver = repoResolver;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Pattern validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates an array of Asterisk dial patterns.
    /// Returns a list of error messages (empty if all patterns are valid).
    /// </summary>
    /// <remarks>
    /// Rules:
    /// <list type="bullet">
    /// <item>Must start with <c>_</c> (pattern) OR be an exact match (digits, *, #, + only)</item>
    /// <item>Valid chars after <c>_</c>: X (0-9), Z (1-9), N (2-9), [...] (char class), . (1+), ! (0+), digits 0-9, *, #, +</item>
    /// <item>Max length: 40 characters</item>
    /// <item>No empty/whitespace-only patterns</item>
    /// </list>
    /// </remarks>
    public static List<string> ValidatePatterns(string[] patterns)
    {
        var errors = new List<string>();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errors.Add("Pattern cannot be empty or whitespace.");
                continue;
            }

            if (pattern.Length > MaxPatternLength)
            {
                errors.Add($"Pattern '{pattern}' exceeds maximum length of {MaxPatternLength} characters.");
                continue;
            }

            if (pattern.StartsWith('_'))
            {
                // Asterisk pattern: must have at least one char after underscore,
                // and cannot start with a second underscore.
                var body = pattern[1..];
                if (body.Length == 0)
                {
                    errors.Add("Pattern '_' is incomplete; at least one character must follow the underscore.");
                    continue;
                }

                if (body.StartsWith('_'))
                {
                    errors.Add($"Pattern '{pattern}' is invalid; double underscore is not allowed.");
                    continue;
                }

                if (!PatternBodyRegex().IsMatch(body))
                {
                    errors.Add($"Pattern '{pattern}' contains invalid characters.");
                }
            }
            else
            {
                // Exact match: only digits, *, #, + allowed
                if (!ExactMatchRegex().IsMatch(pattern))
                {
                    errors.Add($"Exact-match pattern '{pattern}' must contain only digits, *, #, or +.");
                }
            }
        }

        return errors;
    }

    // -----------------------------------------------------------------------
    // CRUD
    // -----------------------------------------------------------------------

    /// <summary>Gets all pattern groups for a server.</summary>
    public async Task<List<CosPatternGroup>> GetGroupsAsync(string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetPatternGroupRepository(serverId);
        var groups = await repo.GetGroupsAsync(serverId, ct);
        CosPatternGroupLog.GetGroups(_logger, serverId, groups.Count);
        return groups;
    }

    /// <summary>Creates a pattern group after validation.</summary>
    public async Task<(bool Success, string? Error)> CreateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group.Name))
            return (false, "Name is required.");

        if (group.Patterns.Length == 0)
            return (false, "At least one pattern is required.");

        var patternErrors = ValidatePatterns(group.Patterns);
        if (patternErrors.Count > 0)
            return (false, string.Join(" ", patternErrors));

        var repo = _repoResolver.GetPatternGroupRepository(group.ServerId);

        var existing = await repo.GetGroupsAsync(group.ServerId, ct);
        if (existing.Any(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            return (false, "A pattern group with this name already exists on the server.");

        await repo.CreateGroupAsync(group, ct);
        CosPatternGroupLog.Created(_logger, group.ServerId, group.Name);
        return (true, null);
    }

    /// <summary>Updates a pattern group after validation.</summary>
    public async Task<(bool Success, string? Error)> UpdateGroupAsync(CosPatternGroup group, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group.Name))
            return (false, "Name is required.");

        if (group.Patterns.Length == 0)
            return (false, "At least one pattern is required.");

        var patternErrors = ValidatePatterns(group.Patterns);
        if (patternErrors.Count > 0)
            return (false, string.Join(" ", patternErrors));

        var repo = _repoResolver.GetPatternGroupRepository(group.ServerId);
        var success = await repo.UpdateGroupAsync(group, ct);
        if (!success) return (false, "Pattern group not found.");

        CosPatternGroupLog.Updated(_logger, group.ServerId, group.Id);
        return (true, null);
    }

    /// <summary>Deletes a pattern group if not referenced by any COS level.</summary>
    public async Task<(bool Success, string? Error)> DeleteGroupAsync(int id, string serverId, CancellationToken ct = default)
    {
        var repo = _repoResolver.GetPatternGroupRepository(serverId);

        if (await repo.IsGroupReferencedAsync(id, ct))
            return (false, "Pattern group is referenced by one or more COS levels and cannot be deleted.");

        var success = await repo.DeleteGroupAsync(id, ct);
        if (!success) return (false, "Pattern group not found.");

        CosPatternGroupLog.Deleted(_logger, serverId, id);
        return (true, null);
    }
}
