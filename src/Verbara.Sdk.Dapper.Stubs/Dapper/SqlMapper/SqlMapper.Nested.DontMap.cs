namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.DontMap</c>. Real Dapper declares this as a nested-private
    /// marker class (verified via reflection on Dapper 2.1.72 — not exported, not an Attribute).
    /// Mirrored as <c>internal</c> here so it occupies the same slot in the type hierarchy without
    /// leaking into the public API surface.
    /// </summary>
    internal sealed class DontMap
    {
        public DontMap() { }
    }
}
