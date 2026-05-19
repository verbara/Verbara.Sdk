using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.Link&lt;TKey,TValue&gt;</c>. Real Dapper uses this as a
    /// lock-free linked-list cache primitive. No generic constraint in real Dapper
    /// (TKey/TValue both unconstrained). Static helpers throw via canonical template.
    /// </summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Drop-in mirror of Dapper.SqlMapper.Link<TKey,TValue> — surface MUST match real Dapper, including its static helpers.")]
    public sealed class Link<TKey, TValue>
    {
        // Real Dapper's ctor is private — surface tests treat that as "not on the API".
        private Link() { }

        public TKey Key => default!;

        public TValue Value => default!;

        public Link<TKey, TValue>? Tail => null;

        private const string StubMessage =
            "Dapper.SqlMapper.Link<TKey,TValue> stub — Dapper.AOT did not intercept the parent call site. " +
            "See: https://aot.dapperlib.dev/gettingstarted";

        public static void Clear(ref Link<TKey, TValue>? head)
            => throw new NotSupportedException(StubMessage);

        public static bool TryGet(Link<TKey, TValue>? link, TKey key, out TValue value)
            => throw new NotSupportedException(StubMessage);

        public static bool TryAdd(ref Link<TKey, TValue>? head, TKey key, ref TValue value)
            => throw new NotSupportedException(StubMessage);
    }
}
