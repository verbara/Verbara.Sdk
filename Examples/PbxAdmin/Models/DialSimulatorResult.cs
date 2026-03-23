namespace PbxAdmin.Models;

public sealed record DialSimulatorResult(
    DialSimulatorVerdict Verdict,
    string? MatchingRuleName,
    string? MatchingPattern,
    string? MatchingPatternGroup,
    List<DialSimulatorFlagCheck> FlagChecks,
    string? TrunkName,
    string? EffectiveDialString,
    string? ResolvedCosName,
    string? TimeOverrideApplied,
    string? ErrorMessage,
    List<string> TraceLog);

public enum DialSimulatorVerdict { Allowed, Denied, Error }

public sealed record DialSimulatorFlagCheck(
    string FlagName,
    bool PatternMatches,
    bool FlagAllowed,
    string Result);
