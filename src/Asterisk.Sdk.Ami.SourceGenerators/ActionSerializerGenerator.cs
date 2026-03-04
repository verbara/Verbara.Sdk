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
/// Source generator that creates AOT-compatible serializers for ManagerAction subclasses.
/// Scans for classes with [AsteriskMapping] and generates Write methods without reflection.
/// </summary>
[Generator]
public sealed class ActionSerializerGenerator : IIncrementalGenerator
{
    private const string AsteriskMappingFqn =
        "Asterisk.Sdk.Attributes.AsteriskMappingAttribute";

    private const string ManagerActionFqn =
        "Asterisk.Sdk.ManagerAction";

    private const string HasExtraFieldsFqn =
        "Asterisk.Sdk.IHasExtraFields";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var actionInfos = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AsteriskMappingFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractActionInfo(ctx, ct))
            .Where(static info => info != null)
            .Select(static (info, _) => info!.Value);

        var collected = actionInfos.Collect();

        context.RegisterSourceOutput(collected, static (spc, actions) =>
        {
            if (actions.IsEmpty)
                return;

            var source = GenerateSerializer(actions);
            spc.AddSource("GeneratedActionSerializer.g.cs", source);
        });
    }

    private static ActionInfo? ExtractActionInfo(
        GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        // Must inherit from ManagerAction
        if (!InheritsFrom(symbol, ManagerActionFqn))
            return null;

        var attr = ctx.Attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == AsteriskMappingFqn);
        if (attr == null || attr.ConstructorArguments.Length == 0)
            return null;

        var mappingName = attr.ConstructorArguments[0].Value as string;
        if (string.IsNullOrEmpty(mappingName))
            return null;

        var fullyQualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Collect all serializable properties from the type and its bases
        // (excluding ManagerAction.ActionId which is handled separately by the protocol writer)
        var properties = new List<PropertyInfo>();
        CollectProperties(symbol, properties, ct);

        var hasExtraFields = symbol.AllInterfaces.Any(i => i.ToDisplayString() == HasExtraFieldsFqn);

        return new ActionInfo(
            mappingName!,
            fullyQualifiedName,
            symbol.Name,
            properties.ToArray(),
            hasExtraFields);
    }

    private static void CollectProperties(
        INamedTypeSymbol symbol,
        List<PropertyInfo> properties,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Walk from the leaf to the base, but stop at ManagerAction (its ActionId is not serialized here)
        var typeChain = new List<INamedTypeSymbol>();
        var current = symbol;
        while (current != null && current.ToDisplayString() != ManagerActionFqn
                               && current.SpecialType != SpecialType.System_Object)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }

        // Process from base to leaf so base properties come first
        typeChain.Reverse();

        var seen = new HashSet<string>();
        foreach (var type in typeChain)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol prop
                    && prop.DeclaredAccessibility == Accessibility.Public
                    && prop.GetMethod != null
                    && !prop.IsStatic
                    && !prop.IsIndexer
                    && seen.Add(prop.Name))
                {
                    var propType = ClassifyPropertyType(prop.Type);
                    if (propType != PropertyType.Unsupported)
                    {
                        // Check if property has [AsteriskMapping] for custom field name
                        var fieldName = GetFieldName(prop);
                        properties.Add(new PropertyInfo(prop.Name, fieldName, propType));
                    }
                }
            }
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
        // Default: property name is the AMI field name
        return prop.Name;
    }

    private static PropertyType ClassifyPropertyType(ITypeSymbol type)
    {
        // Handle Nullable<T>
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

        // Nullable string
        if (type.SpecialType == SpecialType.System_String)
            return PropertyType.String;

        // Non-nullable primitives (unlikely in this codebase, but support them)
        switch (type.SpecialType)
        {
            case SpecialType.System_Int32: return PropertyType.NullableInt;
            case SpecialType.System_Int64: return PropertyType.NullableLong;
            case SpecialType.System_Boolean: return PropertyType.NullableBool;
            case SpecialType.System_Double: return PropertyType.NullableDouble;
        }

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

    private static string GenerateSerializer(ImmutableArray<ActionInfo> actions)
    {
        var sb = new StringBuilder(32768);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Asterisk.Sdk;");
        sb.AppendLine();
        sb.AppendLine("namespace Asterisk.Sdk.Ami.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// AOT-compatible action serializer. Converts ManagerAction instances");
        sb.AppendLine("/// to AMI key-value pairs without reflection.");
        sb.AppendLine("/// Auto-generated by ActionSerializerGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class GeneratedActionSerializer");
        sb.AppendLine("{");

        // Sort for deterministic output
        var sorted = actions.OrderBy(a => a.MappingName).ToArray();

        // GetActionName method
        sb.AppendLine("    /// <summary>Gets the AMI action name for the given action instance.</summary>");
        sb.AppendLine("    public static string GetActionName(ManagerAction action) => action switch");
        sb.AppendLine("    {");
        foreach (var action in sorted)
        {
            sb.AppendLine($"        {action.FullyQualifiedTypeName} => \"{EscapeString(action.MappingName)}\",");
        }
        sb.AppendLine("        _ => action.GetType().Name.Replace(\"Action\", \"\")");
        sb.AppendLine("    };");
        sb.AppendLine();

        // Serialize method
        sb.AppendLine("    /// <summary>Serializes the action's properties to AMI key-value pairs.</summary>");
        sb.AppendLine("    public static IEnumerable<KeyValuePair<string, string>> Serialize(ManagerAction action) => action switch");
        sb.AppendLine("    {");
        foreach (var action in sorted)
        {
            if (action.Properties.Length == 0 && !action.HasExtraFields)
            {
                sb.AppendLine($"        {action.FullyQualifiedTypeName} => Array.Empty<KeyValuePair<string, string>>(),");
            }
            else
            {
                sb.AppendLine($"        {action.FullyQualifiedTypeName} a => Serialize{action.ClassName}(a),");
            }
        }
        sb.AppendLine("        _ => Array.Empty<KeyValuePair<string, string>>()");
        sb.AppendLine("    };");
        sb.AppendLine();

        // Per-action serialization methods
        foreach (var action in sorted)
        {
            if (action.Properties.Length == 0 && !action.HasExtraFields)
                continue;

            sb.AppendLine($"    private static IEnumerable<KeyValuePair<string, string>> Serialize{action.ClassName}({action.FullyQualifiedTypeName} a)");
            sb.AppendLine("    {");

            foreach (var prop in action.Properties)
            {
                switch (prop.Type)
                {
                    case PropertyType.String:
                        sb.AppendLine($"        if (a.{prop.Name} is not null) yield return new(\"{EscapeString(prop.FieldName)}\", a.{prop.Name});");
                        break;
                    case PropertyType.NullableInt:
                        sb.AppendLine($"        if (a.{prop.Name} is not null) yield return new(\"{EscapeString(prop.FieldName)}\", a.{prop.Name}.Value.ToString(CultureInfo.InvariantCulture));");
                        break;
                    case PropertyType.NullableLong:
                        sb.AppendLine($"        if (a.{prop.Name} is not null) yield return new(\"{EscapeString(prop.FieldName)}\", a.{prop.Name}.Value.ToString(CultureInfo.InvariantCulture));");
                        break;
                    case PropertyType.NullableDouble:
                        sb.AppendLine($"        if (a.{prop.Name} is not null) yield return new(\"{EscapeString(prop.FieldName)}\", a.{prop.Name}.Value.ToString(CultureInfo.InvariantCulture));");
                        break;
                    case PropertyType.NullableBool:
                        sb.AppendLine($"        if (a.{prop.Name} is not null) yield return new(\"{EscapeString(prop.FieldName)}\", a.{prop.Name}.Value ? \"true\" : \"false\");");
                        break;
                }
            }

            if (action.HasExtraFields)
            {
                sb.AppendLine("        foreach (var kvp in ((global::Asterisk.Sdk.IHasExtraFields)a).GetExtraFields())");
                sb.AppendLine("            yield return kvp;");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
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

    private readonly struct ActionInfo
    {
        public readonly string MappingName;
        public readonly string FullyQualifiedTypeName;
        public readonly string ClassName;
        public readonly PropertyInfo[] Properties;
        public readonly bool HasExtraFields;

        public ActionInfo(string mappingName, string fullyQualifiedTypeName, string className, PropertyInfo[] properties, bool hasExtraFields)
        {
            MappingName = mappingName;
            FullyQualifiedTypeName = fullyQualifiedTypeName;
            ClassName = className;
            Properties = properties;
            HasExtraFields = hasExtraFields;
        }
    }
}
