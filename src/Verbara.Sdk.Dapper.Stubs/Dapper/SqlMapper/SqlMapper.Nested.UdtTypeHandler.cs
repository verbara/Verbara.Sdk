using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>
    /// AOT-clean stub of <c>SqlMapper.UdtTypeHandler</c>. Real Dapper uses this for SQL Server
    /// User-Defined Types (UDTs). The two ITypeHandler members are explicit-impl in real Dapper
    /// (not part of the public surface enumerated by reflection); ctor takes the UDT type name.
    ///
    /// Note: AOT-trim annotations are intentionally omitted on the explicit-interface members —
    /// IL2046/IL3051 require parity with ITypeHandler, which lacks them (mirror of real Dapper).
    /// </summary>
    public sealed class UdtTypeHandler : ITypeHandler
    {
        public UdtTypeHandler(string udtTypeName)
        {
            // No-op store: stubs don't act on this value.
            _ = udtTypeName;
        }

        // Explicit interface implementations — match real Dapper's surface (non-public IL).
        void ITypeHandler.SetValue(IDbDataParameter parameter, object? value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.UdtTypeHandler.ITypeHandler.SetValue stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");

        object? ITypeHandler.Parse(Type destinationType, object value)
            => throw new NotSupportedException(
                "Dapper.SqlMapper.UdtTypeHandler.ITypeHandler.Parse stub — Dapper.AOT did not intercept the parent call site. " +
                "See: https://aot.dapperlib.dev/gettingstarted");
    }
}
