using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.StringTypeHandler&lt;T&gt;</c>. Base class for type handlers
    /// that convert to/from a SQL string column. Real Dapper provides Format/Parse(string) as
    /// protected abstract; SetValue/Parse(object) are non-abstract overrides that bridge through
    /// Format/Parse(string). Stubs preserve that exact shape.
    ///
    /// Note: AOT-trim annotations are intentionally omitted on override members — IL2046/IL3051
    /// require parity with the base, and our TypeHandler&lt;T&gt; base lacks them (mirror of real
    /// Dapper's contract). The throw-body provides equivalent runtime safety.
    /// </summary>
    public abstract class StringTypeHandler<T> : TypeHandler<T>
    {
        // Real Dapper exposes only a private parameterless ctor.
        protected StringTypeHandler() { }

        /// <summary>Parse a non-null string value into the target T. Implemented by derived class.</summary>
        protected abstract T? Parse(string xml);

        /// <summary>Format a non-null T into its SQL string representation. Implemented by derived class.</summary>
        protected abstract string Format(T xml);

        public override void SetValue(IDbDataParameter parameter, T? value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.StringTypeHandler<T>.SetValue stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        public override T? Parse(object value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.StringTypeHandler<T>.Parse stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");
    }
}
