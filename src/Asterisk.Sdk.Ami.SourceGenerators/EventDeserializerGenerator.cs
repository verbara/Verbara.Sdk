#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Asterisk.Sdk.Ami.SourceGenerators;

/// <summary>
/// Source generator that creates AOT-compatible deserializers for ManagerEvent subclasses.
/// Replaces the reflection-based EventBuilderImpl from asterisk-java.
/// Generates per-type property assignment from AmiMessage fields.
/// </summary>
[Generator]
public sealed class EventDeserializerGenerator : IIncrementalGenerator
{
    private const string AsteriskMappingFqn =
        "Asterisk.Sdk.Attributes.AsteriskMappingAttribute";

    private const string ManagerEventFqn =
        "Asterisk.Sdk.ManagerEvent";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var eventInfos = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AsteriskMappingFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractEventInfo(ctx, ct))
            .Where(static info => info != null)
            .Select(static (info, _) => info!.Value);

        var collected = eventInfos.Collect();

        context.RegisterSourceOutput(collected, static (spc, events) =>
        {
            if (events.IsEmpty)
                return;

            var source = GenerateDeserializer(events);
            spc.AddSource("GeneratedEventDeserializer.g.cs", source);
        });
    }

    private static EventInfo? ExtractEventInfo(
        GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        if (!InheritsFrom(symbol, ManagerEventFqn))
            return null;

        var attr = ctx.Attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == AsteriskMappingFqn);
        if (attr == null || attr.ConstructorArguments.Length == 0)
            return null;

        var mappingName = attr.ConstructorArguments[0].Value as string;
        if (string.IsNullOrEmpty(mappingName))
            return null;

        var fullyQualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Collect type hierarchy for intermediate base class handling
        var hierarchy = new List<TypeLayerInfo>();
        CollectHierarchy(symbol, hierarchy, ct);

        return new EventInfo(
            mappingName!,
            fullyQualifiedName,
            symbol.Name,
            hierarchy.ToArray());
    }

    /// <summary>
    /// Collects the type hierarchy from the leaf class up to (but not including)
    /// ManagerEvent. Each layer contains only the properties declared at that level.
    /// The list is ordered from base to leaf.
    /// </summary>
    private static void CollectHierarchy(
        INamedTypeSymbol symbol,
        List<TypeLayerInfo> hierarchy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var typeChain = new List<INamedTypeSymbol>();
        var current = symbol;
        while (current != null
               && current.ToDisplayString() != ManagerEventFqn
               && current.SpecialType != SpecialType.System_Object)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }

        // Reverse: base first, leaf last
        typeChain.Reverse();

        foreach (var type in typeChain)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var properties = new List<PropertyInfo>();

            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol prop
                    && prop.DeclaredAccessibility == Accessibility.Public
                    && prop.SetMethod != null
                    && prop.SetMethod.DeclaredAccessibility == Accessibility.Public
                    && !prop.IsStatic
                    && !prop.IsIndexer)
                {
                    // Skip ManagerEvent base properties (handled separately)
                    // and the RawFields property
                    var propName = prop.Name;
                    if (propName == "RawFields" || propName == "EventType"
                        || propName == "Privilege" || propName == "UniqueId"
                        || propName == "Timestamp")
                        continue;

                    var propType = ClassifyPropertyType(prop.Type);
                    if (propType != PropertyType.Unsupported)
                    {
                        var fieldName = GetFieldName(prop);
                        properties.Add(new PropertyInfo(propName, fieldName, propType));
                    }
                }
            }

            hierarchy.Add(new TypeLayerInfo(
                fqn,
                type.Name,
                SymbolEqualityComparer.Default.Equals(type, symbol), // isLeaf
                properties.ToArray()));
        }
    }

    private static string GetFieldName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() ==
                "Asterisk.Sdk.Attributes.AsteriskMappingAttribute"
                && attr.ConstructorArguments.Length > 0)
            {
                if (attr.ConstructorArguments[0].Value is string name && !string.IsNullOrEmpty(name))
                    return name;
            }
        }
        return prop.Name;
    }

    private static PropertyType ClassifyPropertyType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1)
        {
            var inner = named.TypeArguments[0];
            switch (inner.SpecialType)
            {
                case SpecialType.System_Int32: return PropertyType.NullableInt;
                case SpecialType.System_Int64: return PropertyType.NullableLong;
                case SpecialType.System_Boolean: return PropertyType.NullableBool;
                case SpecialType.System_Double: return PropertyType.NullableDouble;
            }
            return PropertyType.Unsupported;
        }

        if (type.SpecialType == SpecialType.System_String)
            return PropertyType.String;

        return PropertyType.Unsupported;
    }

    private static bool InheritsFrom(INamedTypeSymbol symbol, string baseTypeFqn)
    {
        var current = symbol.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString() == baseTypeFqn)
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static string GenerateDeserializer(ImmutableArray<EventInfo> events)
    {
        var sb = new StringBuilder(65536);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0618 // Obsolete types — generated registry must still reference them");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Asterisk.Sdk;");
        sb.AppendLine("using Asterisk.Sdk.Ami.Internal;");
        sb.AppendLine();
        sb.AppendLine("namespace Asterisk.Sdk.Ami.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// AOT-compatible event deserializer. Populates ManagerEvent instances");
        sb.AppendLine("/// from AmiMessage fields without reflection.");
        sb.AppendLine("/// Auto-generated by EventDeserializerGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class GeneratedEventDeserializer");
        sb.AppendLine("{");

        // Main Deserialize method
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Deserializes an AmiMessage into a typed ManagerEvent instance.");
        sb.AppendLine("    /// Uses GeneratedEventRegistry to create the correct type, then populates");
        sb.AppendLine("    /// all properties from the message fields.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static ManagerEvent Deserialize(AmiMessage msg)");
        sb.AppendLine("    {");
        sb.AppendLine("        var eventName = msg.EventType ?? \"\";");
        sb.AppendLine("        var evt = GeneratedEventRegistry.Create(eventName) ?? new ManagerEvent();");
        sb.AppendLine();
        sb.AppendLine("        // Set base ManagerEvent properties");
        sb.AppendLine("        evt.EventType = eventName;");
        sb.AppendLine("        evt.UniqueId = msg[\"Uniqueid\"];");
        sb.AppendLine("        evt.Privilege = msg[\"Privilege\"];");
        sb.AppendLine("        if (double.TryParse(msg[\"Timestamp\"], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts))");
        sb.AppendLine("            evt.Timestamp = ts;");
        sb.AppendLine();

        var sorted = events.OrderBy(e => e.MappingName).ToArray();

        // Collect all unique intermediate base types (non-leaf layers) across all events
        var intermediateTypes = new Dictionary<string, IntermediateBaseInfo>();
        foreach (var evt in sorted)
        {
            foreach (var layer in evt.Hierarchy)
            {
                if (!layer.IsLeaf && layer.Properties.Length > 0)
                {
                    if (!intermediateTypes.ContainsKey(layer.FullyQualifiedTypeName))
                    {
                        intermediateTypes[layer.FullyQualifiedTypeName] = new IntermediateBaseInfo(
                            layer.FullyQualifiedTypeName,
                            layer.ClassName,
                            layer.Properties);
                    }
                }
            }
        }

        // Generate intermediate base class property assignment
        if (intermediateTypes.Count > 0)
        {
            sb.AppendLine("        // Set intermediate base class properties");
            foreach (var kvp in intermediateTypes.OrderBy(k => k.Key))
            {
                var baseInfo = kvp.Value;
                var varName = ToCamelCase(baseInfo.ClassName);
                sb.AppendLine($"        if (evt is {baseInfo.FullyQualifiedTypeName} {varName})");
                sb.AppendLine("        {");
                foreach (var prop in baseInfo.Properties)
                {
                    EmitPropertyDeserialization(sb, varName, prop, "            ");
                }
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        // Generate leaf class property assignment via switch
        var leafEvents = sorted.Where(e =>
        {
            var leafLayer = e.Hierarchy.LastOrDefault();
            return leafLayer.Properties != null && leafLayer.Properties.Length > 0 && leafLayer.IsLeaf;
        }).ToArray();

        if (leafEvents.Length > 0)
        {
            sb.AppendLine("        // Set leaf class properties");
            sb.AppendLine("        switch (evt)");
            sb.AppendLine("        {");

            foreach (var evt in leafEvents)
            {
                var leafLayer = evt.Hierarchy.Last();
                sb.AppendLine($"            case {evt.FullyQualifiedTypeName} e:");
                sb.AppendLine("            {");
                foreach (var prop in leafLayer.Properties)
                {
                    EmitPropertyDeserialization(sb, "e", prop, "                ");
                }
                sb.AppendLine("                break;");
                sb.AppendLine("            }");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        // Set raw fields for passthrough access");
        sb.AppendLine("        evt.RawFields = msg.Fields;");
        sb.AppendLine();
        sb.AppendLine("        return evt;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitPropertyDeserialization(
        StringBuilder sb, string varName, PropertyInfo prop, string indent)
    {
        var fieldAccess = $"msg[\"{EscapeString(prop.FieldName)}\"]";

        switch (prop.Type)
        {
            case PropertyType.String:
                sb.AppendLine($"{indent}{varName}.{prop.Name} = {fieldAccess};");
                break;

            case PropertyType.NullableInt:
                sb.AppendLine($"{indent}if (int.TryParse({fieldAccess}, NumberStyles.Integer, CultureInfo.InvariantCulture, out var {ToLocalVar(prop.Name)}Int))");
                sb.AppendLine($"{indent}    {varName}.{prop.Name} = {ToLocalVar(prop.Name)}Int;");
                break;

            case PropertyType.NullableLong:
                sb.AppendLine($"{indent}if (long.TryParse({fieldAccess}, NumberStyles.Integer, CultureInfo.InvariantCulture, out var {ToLocalVar(prop.Name)}Long))");
                sb.AppendLine($"{indent}    {varName}.{prop.Name} = {ToLocalVar(prop.Name)}Long;");
                break;

            case PropertyType.NullableDouble:
                sb.AppendLine($"{indent}if (double.TryParse({fieldAccess}, NumberStyles.Float, CultureInfo.InvariantCulture, out var {ToLocalVar(prop.Name)}Dbl))");
                sb.AppendLine($"{indent}    {varName}.{prop.Name} = {ToLocalVar(prop.Name)}Dbl;");
                break;

            case PropertyType.NullableBool:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var {ToLocalVar(prop.Name)}Str = {fieldAccess};");
                sb.AppendLine($"{indent}    if ({ToLocalVar(prop.Name)}Str is not null)");
                sb.AppendLine($"{indent}        {varName}.{prop.Name} = string.Equals({ToLocalVar(prop.Name)}Str, \"1\", StringComparison.Ordinal) || string.Equals({ToLocalVar(prop.Name)}Str, \"true\", StringComparison.OrdinalIgnoreCase);");
                sb.AppendLine($"{indent}}}");
                break;
        }
    }

    /// <summary>
    /// Converts a property name to a unique local variable name (camelCase).
    /// </summary>
    private static string ToLocalVar(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return "v";
        return char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
    }

    /// <summary>
    /// Converts a class name to camelCase for use as a variable name.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "v";
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string EscapeString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private enum PropertyType
    {
        Unsupported,
        String,
        NullableInt,
        NullableLong,
        NullableBool,
        NullableDouble
    }

    private readonly struct PropertyInfo
    {
        public readonly string Name;
        public readonly string FieldName;
        public readonly PropertyType Type;

        public PropertyInfo(string name, string fieldName, PropertyType type)
        {
            Name = name;
            FieldName = fieldName;
            Type = type;
        }
    }

    private readonly struct TypeLayerInfo
    {
        public readonly string FullyQualifiedTypeName;
        public readonly string ClassName;
        public readonly bool IsLeaf;
        public readonly PropertyInfo[] Properties;

        public TypeLayerInfo(string fqn, string className, bool isLeaf, PropertyInfo[] properties)
        {
            FullyQualifiedTypeName = fqn;
            ClassName = className;
            IsLeaf = isLeaf;
            Properties = properties;
        }
    }

    private readonly struct IntermediateBaseInfo
    {
        public readonly string FullyQualifiedTypeName;
        public readonly string ClassName;
        public readonly PropertyInfo[] Properties;

        public IntermediateBaseInfo(string fqn, string className, PropertyInfo[] properties)
        {
            FullyQualifiedTypeName = fqn;
            ClassName = className;
            Properties = properties;
        }
    }

    private readonly struct EventInfo
    {
        public readonly string MappingName;
        public readonly string FullyQualifiedTypeName;
        public readonly string ClassName;
        public readonly TypeLayerInfo[] Hierarchy;

        public EventInfo(string mappingName, string fqn, string className, TypeLayerInfo[] hierarchy)
        {
            MappingName = mappingName;
            FullyQualifiedTypeName = fqn;
            ClassName = className;
            Hierarchy = hierarchy;
        }
    }
}
