using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.GridReader</c>. Returned by QueryMultiple to read multiple
    /// result sets sequentially. All methods throw via the canonical stub template; consumers
    /// must rely on Dapper.AOT interceptors replacing QueryMultiple's call site.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Drop-in mirror of Dapper.SqlMapper.GridReader — instance shape MUST match real Dapper. Stubs throw NotSupportedException; in real Dapper these methods read instance state.")]
    public sealed class GridReader : IDisposable
    {
        // Internal ctor: not instantiable from outside.
        internal GridReader() { }

        public bool IsConsumed => false;

        public IDbCommand? Command { get; set; }

        private const string StubMessage =
            "Dapper.SqlMapper.GridReader stub — Dapper.AOT did not intercept the parent QueryMultiple call site. " +
            "See: https://aot.dapperlib.dev/gettingstarted";

        private const string RequiresDynamicCodeMessage =
            "Real Dapper materializes rows via DynamicMethod. Dapper.AOT interceptors replace QueryMultiple call sites entirely.";

        private const string RequiresUnreferencedCodeMessage =
            "Real Dapper reflects over row type properties. Dapper.AOT interceptors replace this with statically-generated RowFactory<T>.";

        // ---- Untyped sync reads (object) ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<object> Read(bool buffered = true) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadFirst() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadFirstOrDefault() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadSingle() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadSingleOrDefault() => throw new NotSupportedException(StubMessage);

        // ---- Type-arg sync reads ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<object> Read(Type type, bool buffered = true) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadFirst(Type type) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadFirstOrDefault(Type type) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadSingle(Type type) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public object ReadSingleOrDefault(Type type) => throw new NotSupportedException(StubMessage);

        // ---- Generic sync reads ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<T> Read<T>(bool buffered = true) => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public T ReadFirst<T>() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public T ReadFirstOrDefault<T>() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public T ReadSingle<T>() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public T ReadSingleOrDefault<T>() => throw new NotSupportedException(StubMessage);

        // ---- Multi-mapping sync reads (2..7 + dynamic-types overload) ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TReturn>(Func<TFirst, TSecond, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TThird, TReturn>(Func<TFirst, TSecond, TThird, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TThird, TFourth, TReturn>(Func<TFirst, TSecond, TThird, TFourth, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn>(Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn>(Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> func, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IEnumerable<TReturn> Read<TReturn>(Type[] types, Func<object[], TReturn> map, string splitOn = "id", bool buffered = true)
            => throw new NotSupportedException(StubMessage);

        // ---- Untyped async reads ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<IEnumerable<object>> ReadAsync(bool buffered = true)
            => Task.FromException<IEnumerable<object>>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadFirstAsync() => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadFirstOrDefaultAsync() => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadSingleAsync() => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadSingleOrDefaultAsync() => Task.FromException<object>(new NotSupportedException(StubMessage));

        // ---- Type-arg async reads ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<IEnumerable<object>> ReadAsync(Type type, bool buffered = true)
            => Task.FromException<IEnumerable<object>>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadFirstAsync(Type type) => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadFirstOrDefaultAsync(Type type) => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadSingleAsync(Type type) => Task.FromException<object>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<object> ReadSingleOrDefaultAsync(Type type) => Task.FromException<object>(new NotSupportedException(StubMessage));

        // ---- Generic async reads ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<IEnumerable<T>> ReadAsync<T>(bool buffered = true)
            => Task.FromException<IEnumerable<T>>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<T> ReadFirstAsync<T>() => Task.FromException<T>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<T> ReadFirstOrDefaultAsync<T>() => Task.FromException<T>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<T> ReadSingleAsync<T>() => Task.FromException<T>(new NotSupportedException(StubMessage));

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public Task<T> ReadSingleOrDefaultAsync<T>() => Task.FromException<T>(new NotSupportedException(StubMessage));

        // ---- Unbuffered async ----
        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IAsyncEnumerable<T> ReadUnbufferedAsync<T>() => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
        public IAsyncEnumerable<object> ReadUnbufferedAsync() => throw new NotSupportedException(StubMessage);

        public ValueTask DisposeAsync() => default;

        public void Dispose() { /* no-op */ }
    }
}
