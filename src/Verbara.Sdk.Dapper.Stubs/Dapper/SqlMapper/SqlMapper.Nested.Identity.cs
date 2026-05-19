using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.Identity</c>. Real Dapper uses this cache-key class
    /// internally; consumers rarely touch it but it's part of the public surface
    /// (referenced from IDynamicParameters.AddParameters). All methods throw via the canonical
    /// stub template. Properties return defaults.
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Drop-in mirror of Dapper.SqlMapper.Identity — instance shape MUST match real Dapper.")]
    public sealed class Identity : IEquatable<Identity>
    {
        // Real Dapper exposes only internal ctors. Our stub matches that by making the
        // default ctor non-public — the surface tests only check methods/properties.
        internal Identity() { }

        public string Sql => string.Empty;
        public CommandType? CommandType => null;
        public int GridIndex => 0;
        public Type? Type => null;
        public Type? ParametersType => null;

        [RequiresDynamicCode("Real Dapper materializes parameters via DynamicMethod. " +
                             "This stub assumes Dapper.AOT interceptors replace the call site. " +
                             "If this body executes, Dapper.AOT did not intercept — verify [module: DapperAot] " +
                             "is applied and InterceptorsPreviewNamespaces MSBuild property includes Dapper.AOT.")]
        [RequiresUnreferencedCode("Real Dapper reflects over row type properties + constructors. " +
                                  "Dapper.AOT interceptors replace this with statically-generated RowFactory<T>.")]
        public Identity ForDynamicParameters(Type type)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.Identity.ForDynamicParameters stub — Dapper.AOT did not intercept this call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        public override bool Equals(object? obj)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.Identity.Equals stub — Dapper.AOT did not intercept this call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        public override int GetHashCode()
            => throw new NotSupportedException(
                "Dapper.SqlMapper.Identity.GetHashCode stub — Dapper.AOT did not intercept this call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        public override string ToString()
            => throw new NotSupportedException(
                "Dapper.SqlMapper.Identity.ToString stub — Dapper.AOT did not intercept this call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        public bool Equals(Identity? other)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.Identity.Equals stub — Dapper.AOT did not intercept this call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");
    }
}
