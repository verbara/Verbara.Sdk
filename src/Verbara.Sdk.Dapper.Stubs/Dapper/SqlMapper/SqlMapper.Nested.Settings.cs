namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.Settings</c>. Real Dapper exposes global toggles like
    /// <c>ApplyNullValues</c>, <c>UseSingleResultOptimization</c>, etc. Stubs ship idempotent
    /// no-op setters: reading returns the default; setting silently discards. This prevents
    /// crashes when consumer startup code touches these toggles but the values have no effect
    /// (Dapper.AOT-generated interceptors don't read them).
    /// </summary>
    public static class Settings
    {
        public static bool UseSingleResultOptimization { get; set; }
        public static bool UseSingleRowOptimization { get; set; }
        public static int? CommandTimeout { get; set; }
        public static bool ApplyNullValues { get; set; }
        public static bool PadListExpansions { get; set; }
        public static int InListStringSplitCount { get; set; } = -1;
        public static bool UseIncrementalPseudoPositionalParameterNames { get; set; }
        public static long FetchSize { get; set; } = -1;
        public static bool SupportLegacyParameterTokens { get; set; }

        /// <summary>No-op: real Dapper resets all toggles to defaults; stubs already use defaults.</summary>
        public static void SetDefaults()
        {
            UseSingleResultOptimization = false;
            UseSingleRowOptimization = false;
            CommandTimeout = null;
            ApplyNullValues = false;
            PadListExpansions = false;
            InListStringSplitCount = -1;
            UseIncrementalPseudoPositionalParameterNames = false;
            FetchSize = -1;
            SupportLegacyParameterTokens = false;
        }
    }
}
