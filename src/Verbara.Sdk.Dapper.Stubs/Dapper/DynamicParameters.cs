using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.DynamicParameters</c>. The parameterless ctor + the typed
/// <see cref="Add(string, object?, DbType?, ParameterDirection?, int?)"/> /
/// <see cref="Add(string, object?, DbType?, ParameterDirection?, int?, byte?, byte?)"/> /
/// <see cref="Get{T}"/> / <see cref="ParameterNames"/> path is runtime-safe (pure dictionary
/// operations). The remaining surface — template ctor, <see cref="AddDynamicParams"/>,
/// <see cref="Output{T}"/>, and the explicit-interface IL hook for
/// <c>IDynamicParameters.AddParameters</c> — throws, since Dapper.AOT must intercept the parent
/// call site to drive parameter binding without runtime reflection.
/// </summary>
/// <remarks>
/// Real Dapper exposes <c>DynamicParameters</c> as unsealed (consumers occasionally subclass it
/// to inject default values); we mirror that.
/// </remarks>
public class DynamicParameters : SqlMapper.IDynamicParameters, SqlMapper.IParameterLookup, SqlMapper.IParameterCallbacks
{
    private readonly Dictionary<string, ParameterEntry> _parameters = new(StringComparer.Ordinal);

    /// <summary>Initialise an empty parameter bag.</summary>
    public DynamicParameters() { }

    /// <summary>
    /// Initialise from <paramref name="template"/> by reflecting over its public properties.
    /// Stub throws — rewrite the call site to use anonymous types or per-property
    /// <see cref="Add(string, object?, DbType?, ParameterDirection?, int?)"/> calls (per
    /// Dapper.AOT diagnostic <c>DAP015</c>).
    /// </summary>
    [RequiresDynamicCode("Real Dapper iterates the template's properties via reflection. Per DAP015, rewrite this call site to use anonymous types or explicit Add() calls.")]
    [RequiresUnreferencedCode("Real Dapper reflects over template's public properties.")]
    [SuppressMessage("Performance", "CA1019:Define accessors for attribute arguments",
        Justification = "Not an attribute — false positive on stub ctor.")]
    public DynamicParameters(object? template)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters(object template) stub — Dapper.AOT did not intercept the parent call site. " +
            "Rewrite to anonymous types or explicit Add() per DAP015.");

    /// <summary>
    /// When <c>true</c>, parameters not referenced by the SQL text are removed before execution.
    /// Stored on the stub but otherwise inert — Dapper.AOT inspects the value via interceptor codegen.
    /// </summary>
    public bool RemoveUnused { get; set; }

    /// <summary>The names of every parameter that has been added via <see cref="Add(string, object?, DbType?, ParameterDirection?, int?)"/>.</summary>
    public IEnumerable<string> ParameterNames => _parameters.Keys;

    /// <summary>
    /// Real Dapper merges anonymous-property names from <paramref name="param"/> into this bag.
    /// Stub throws — rewrite per <c>DAP015</c>.
    /// </summary>
    [RequiresDynamicCode("Real Dapper iterates the parameter object's properties via reflection.")]
    [RequiresUnreferencedCode("Real Dapper reflects over the parameter object's public properties.")]
    public void AddDynamicParams(object? param)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters.AddDynamicParams stub — rewrite call site per DAP015.");

    /// <summary>
    /// Add a parameter without precision/scale. Runtime-safe — stores the entry in the underlying
    /// dictionary; Dapper.AOT reads it back at execution time.
    /// </summary>
    public void Add(string name, object? value, DbType? dbType, ParameterDirection? direction, int? size)
        => _parameters[name] = new ParameterEntry(name, value, dbType, direction, size, null, null);

    /// <summary>
    /// Add a parameter including precision/scale. Runtime-safe — stores the entry in the
    /// underlying dictionary; Dapper.AOT reads it back at execution time.
    /// </summary>
    public void Add(string name, object? value = null, DbType? dbType = null, ParameterDirection? direction = null, int? size = null, byte? precision = null, byte? scale = null)
        => _parameters[name] = new ParameterEntry(name, value, dbType, direction, size, precision, scale);

    /// <summary>
    /// Retrieve a previously-added parameter value, typed as <typeparamref name="T"/>. Useful for
    /// reading <see cref="ParameterDirection.Output"/> or <see cref="ParameterDirection.ReturnValue"/>
    /// after execution.
    /// </summary>
    public T? Get<T>(string name)
    {
        if (!_parameters.TryGetValue(name, out var entry))
        {
            throw new KeyNotFoundException($"The parameter '{name}' was not found.");
        }
        return (T?)entry.Value;
    }

    /// <summary>
    /// Configure an output parameter and bind it to the supplied target property. Stub throws —
    /// real Dapper inspects the expression tree to discover the target property, which requires
    /// reflection and is incompatible with trim/AOT.
    /// </summary>
    [RequiresDynamicCode("Real Dapper compiles the expression tree and binds via reflection.")]
    [RequiresUnreferencedCode("Real Dapper reflects over the target type's property to write the output value.")]
    public DynamicParameters Output<T>(T target, Expression<Func<T, object?>> expression, DbType? dbType = null, int? size = null)
        => throw new NotSupportedException("Dapper.DynamicParameters.Output stub — Dapper.AOT did not intercept the parent call site.");

    // -------------------------------------------------------------------------
    // Explicit interface implementations — vanilla Dapper invokes these when the
    // AOT interceptor did not win. They MUST drop the [RequiresDynamicCode]/
    // [RequiresUnreferencedCode] annotations to satisfy IL2046/IL3051 parity
    // with the un-annotated interface declarations.
    // -------------------------------------------------------------------------

    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
        => throw new NotSupportedException(
            "Dapper.DynamicParameters.AddParameters stub — Dapper.AOT did not intercept the parent call site.");

    object? SqlMapper.IParameterLookup.this[string name]
        => _parameters.TryGetValue(name, out var entry) ? entry.Value : null;

    void SqlMapper.IParameterCallbacks.OnCompleted()
    {
        // Real Dapper invokes per-parameter cleanup hooks here (e.g., disposing TVP readers).
        // Stub is a no-op — anything stored via Add() is plain CLR data with no cleanup contract.
    }

    /// <summary>One captured parameter — name + value + ADO.NET decorations.</summary>
    private readonly struct ParameterEntry
    {
        public ParameterEntry(string name, object? value, DbType? dbType, ParameterDirection? direction, int? size, byte? precision, byte? scale)
        {
            Name = name;
            Value = value;
            DbType = dbType;
            Direction = direction;
            Size = size;
            Precision = precision;
            Scale = scale;
        }

        public string Name { get; }
        public object? Value { get; }
        public DbType? DbType { get; }
        public ParameterDirection? Direction { get; }
        public int? Size { get; }
        public byte? Precision { get; }
        public byte? Scale { get; }
    }
}
