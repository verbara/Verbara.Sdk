using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.TypeHandlerCache&lt;T&gt;</c>. Real Dapper uses this as a
    /// per-T cache + dispatch surface for registered ITypeHandler instances. All members throw
    /// via the canonical stub template.
    /// </summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Drop-in mirror of Dapper.SqlMapper.TypeHandlerCache<T> — surface MUST match real Dapper.")]
    public static class TypeHandlerCache<T>
    {
        private const string StubMessage =
            "Dapper.SqlMapper.TypeHandlerCache<T> stub — Dapper.AOT did not intercept the parent call site. " +
            "See: https://aot.dapperlib.dev/gettingstarted";

        [RequiresDynamicCode("Real Dapper invokes typed Parse via DynamicMethod. Dapper.AOT replaces parent call sites.")]
        [RequiresUnreferencedCode("Real Dapper reflects to bridge object → T via the registered ITypeHandler.")]
        public static T Parse(object value)
            => throw new NotSupportedException(StubMessage);

        [RequiresDynamicCode("Real Dapper invokes typed SetValue via DynamicMethod. Dapper.AOT replaces parent call sites.")]
        [RequiresUnreferencedCode("Real Dapper reflects to bridge T → parameter via the registered ITypeHandler.")]
        public static void SetValue(IDbDataParameter parameter, object value)
            => throw new NotSupportedException(StubMessage);
    }
}
