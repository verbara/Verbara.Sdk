namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.LiteralToken</c>. Real Dapper declares this as a
    /// nested-internal readonly struct representing literal substitution tokens in SQL
    /// (<c>{=name}</c>) — verified via reflection on Dapper 2.1.72 (not exported). Mirrored as
    /// <c>internal</c> here so the type occupies the same slot without leaking into the public
    /// API surface.
    /// </summary>
    internal readonly struct LiteralToken
    {
        // Real Dapper's ctor is internal — surface tests treat that as "not on the API".
        // Stub provides an internal ctor for symmetry.
        internal LiteralToken(string token, string member)
        {
            Token = token;
            Member = member;
        }

        public string Token { get; }

        public string Member { get; }
    }
}
