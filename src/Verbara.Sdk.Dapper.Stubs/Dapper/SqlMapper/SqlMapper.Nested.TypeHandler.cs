using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.TypeHandler&lt;T&gt;</c>. Base class for custom type handlers.
    /// Consumers may still subclass this (most common: JsonbHandler implementations) — the
    /// SetValue/Parse abstract methods are real (they'll be invoked by Dapper.AOT-generated
    /// interceptors when the consumer registers a handler). The ITypeHandler explicit-impl
    /// methods throw because Dapper.AOT bypasses the interface dispatch.
    ///
    /// Note: the AOT-trim annotations ([RequiresDynamicCode]/[RequiresUnreferencedCode]) that
    /// the canonical stub template normally carries are deliberately OMITTED from override and
    /// explicit-interface-impl members here — IL2046/IL3051 require annotation parity with the
    /// declaring interface/base and our ITypeHandler interface has none (mirror of real Dapper).
    /// The throw-body itself is unconditional, so trim safety is preserved either way.
    /// </summary>
    public abstract class TypeHandler<T> : ITypeHandler
    {
        // Real Dapper exposes only a private parameterless ctor (protected to derived classes
        // only via the default base.ctor invocation in subclasses; for surface comparison
        // tests, no public ctor is expected).
        protected TypeHandler() { }

        public abstract void SetValue(IDbDataParameter parameter, T? value);

        public abstract T? Parse(object value);

        // ITypeHandler explicit interface impls — Dapper.AOT bypasses these.
        void ITypeHandler.SetValue(IDbDataParameter parameter, object? value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.TypeHandler<T>.ITypeHandler.SetValue stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        object? ITypeHandler.Parse(Type destinationType, object value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.TypeHandler<T>.ITypeHandler.Parse stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");
    }
}
