namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.LiteralToken</c>. Real Dapper uses this to represent
    /// literal substitution tokens in SQL ({=name}). Stubs ship an empty-default struct;
    /// no public ctor exists in real Dapper (internal) — match that surface.
    /// </summary>
    public readonly struct LiteralToken
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
