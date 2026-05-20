using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;



/// <summary>
/// AOT-clean stub of <c>Dapper.DbString</c>. Mirrors the public surface (instance properties +
/// <see cref="AddParameter"/> implementation of <see cref="SqlMapper.ICustomQueryParameter"/>).
/// The runtime behaviour — emitting a typed/sized <see cref="IDbDataParameter"/> on the command —
/// is replaced by Dapper.AOT-generated parameter writers; the stub <see cref="AddParameter"/>
/// throws if the interceptor did not win.
/// </summary>
[SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Drop-in mirror of Dapper.DbString — instance shape MUST match real Dapper. Stub method throws NotSupportedException; in real Dapper this reads instance state.")]
public sealed class DbString : SqlMapper.ICustomQueryParameter
{
    /// <summary>Initialise an empty <see cref="DbString"/>. Properties may be assigned afterwards.</summary>
    public DbString() { }

    /// <summary>Initialise with <paramref name="value"/> and an explicit byte length (default <c>-1</c>).</summary>
    public DbString(string value, int length = -1)
    {
        Value = value;
        Length = length;
    }

    /// <summary>
    /// Global default for <see cref="IsAnsi"/>. Real Dapper exposes this as a static property so
    /// callers can switch the entire process to ANSI defaults.
    /// </summary>
    public static bool IsAnsiDefault { get; set; }

    /// <summary>Per-instance override; defaults to <see cref="IsAnsiDefault"/> at construction.</summary>
    public bool IsAnsi { get; set; }

    /// <summary>When <c>true</c>, emits as a fixed-length (CHAR/NCHAR) rather than variable-length parameter.</summary>
    public bool IsFixedLength { get; set; }

    /// <summary>Byte length; <c>-1</c> for unbounded (the real-Dapper default).</summary>
    public int Length { get; set; } = -1;

    /// <summary>The underlying string value.</summary>
    public string? Value { get; set; }

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;

    /// <summary>
    /// Adds this string as a typed parameter on <paramref name="command"/>. Stub throws — replaced
    /// at compile time by Dapper.AOT-generated parameter writers.
    /// </summary>
    /// <remarks>
    /// Annotations intentionally omitted: <see cref="SqlMapper.ICustomQueryParameter.AddParameter"/>
    /// declares no <c>[RequiresDynamicCode]</c>/<c>[RequiresUnreferencedCode]</c>, so adding them
    /// here would trip IL2046/IL3051. The throw body still guarantees trim safety in practice.
    /// </remarks>
    public void AddParameter(IDbCommand command, string name)
        => throw new NotSupportedException(
            "Dapper.DbString.AddParameter stub — Dapper.AOT did not intercept the parent call site.");
}
