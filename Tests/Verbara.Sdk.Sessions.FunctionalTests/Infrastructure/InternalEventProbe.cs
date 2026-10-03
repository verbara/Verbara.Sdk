using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>Reaches a type's internal members by reflection, so a test compiles before they exist.</summary>
[SuppressMessage("Trimming", "IL2075", Justification = "Test code: reflection over the SDK's own internal members, never trimmed here.")]
[SuppressMessage("AOT", "IL3050", Justification = "Test code: the handler is compiled at run time, never AOT-compiled here.")]
internal static class InternalEventProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Subscribes to the event <paramref name="name"/> declared on the type of <paramref name="source"/>, and records the
    /// first argument of each raise.
    /// </summary>
    public static List<object?> Capture(object source, string name)
    {
        var info = source.GetType().GetEvent(name, AnyInstance)
            ?? throw new InvalidOperationException($"Event not found: {source.GetType().Name}.{name}.");
        var raised = new List<object?>();
        var invoke = info.EventHandlerType!.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var record = (Action<object?>)raised.Add;
        var body = Expression.Invoke(Expression.Constant(record), Expression.Convert(parameters[0], typeof(object)));
        var handler = Expression.Lambda(info.EventHandlerType, body, parameters).Compile();
        info.GetAddMethod(nonPublic: true)!.Invoke(source, [handler]);
        return raised;
    }

    /// <summary>The value of the property <paramref name="name"/> of <paramref name="target"/>.</summary>
    public static object? Read(object? target, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        var property = target.GetType().GetProperty(name, AnyInstance)
            ?? throw new InvalidOperationException($"Property not found: {target.GetType().Name}.{name}.");
        return property.GetValue(target);
    }
}
