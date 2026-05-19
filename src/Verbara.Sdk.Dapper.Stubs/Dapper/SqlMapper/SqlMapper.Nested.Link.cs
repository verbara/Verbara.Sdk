using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.Link&lt;TKey,TValue&gt;</c>. Real Dapper declares this as a
    /// nested-internal lock-free linked-list cache primitive (verified via reflection on Dapper
    /// 2.1.72 — not exported). Mirrored as <c>internal</c> here so the type occupies the same slot
    /// without leaking into the public API surface.
    /// </summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Drop-in mirror of Dapper.SqlMapper.Link<TKey,TValue> — surface MUST match real Dapper, including its static helpers.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Drop-in mirror of Dapper.SqlMapper.Link<TKey,TValue> — instance shape (Key/Value/Tail) MUST match real Dapper.")]
    internal sealed class Link<TKey, TValue>
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
