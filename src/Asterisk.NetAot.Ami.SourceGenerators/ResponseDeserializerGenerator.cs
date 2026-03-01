#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Asterisk.NetAot.Ami.SourceGenerators;

/// <summary>
/// Source generator that creates AOT-compatible deserializers for ManagerResponse subclasses.
/// Generates a static Deserialize method that populates typed response instances from AmiMessage fields.
/// </summary>
[Generator]
public sealed class ResponseDeserializerGenerator : IIncrementalGenerator
{
    private const string AsteriskMappingFqn =
        "Asterisk.NetAot.Abstractions.Attributes.AsteriskMappingAttribute";

    private const string ManagerResponseFqn =
        "Asterisk.NetAot.Abstractions.ManagerResponse";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var responseInfos = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AsteriskMappingFqn,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractResponseInfo(ctx, ct))
            .Where(static info => info != null)
            .Select(static (info, _) => info!.Value);

        var collected = responseInfos.Collect();

        context.RegisterSourceOutput(collected, static (spc, responses) =>
        {
            if (responses.IsEmpty)
                return;

            var source = GenerateDeserializer(responses);
            spc.AddSource("GeneratedResponseDeserializer.g.cs", source);
        });
    }

    private static ResponseInfo? ExtractResponseInfo(
        GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        if (!InheritsFrom(symbol, ManagerResponseFqn))
            return null;

        var attr = ctx.Attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == AsteriskMappingFqn);
        if (attr == null || attr.ConstructorArguments.Length == 0)
            return null;

        var mappingName = attr.ConstructorArguments[0].Value as string;
        if (string.IsNullOrEmpty(mappingName))
            return null;

        var fullyQualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Collect all properties from the type hierarchy (flat: all responses inherit directly from ManagerResponse)
        var properties = new List<PropertyInfo>();
        CollectProperties(symbol, properties, ct);

        return new ResponseInfo(
            mappingName!,
            fullyQualifiedName,
            symbol.Name,
            properties.ToArray());
    }

    private static void CollectProperties(
        INamedTypeSymbol symbol,
        List<PropertyInfo> properties,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var typeChain = new List<INamedTypeSymbol>();
        var current = symbol;
        while (current != null
               && current.ToDisplayString() != ManagerResponseFqn
               && current.SpecialType != SpecialType.System_Object)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }

        typeChain.Reverse();

        var seen = new HashSet<string>();
        foreach (var type in typeChain)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol prop
                    && prop.DeclaredAccessibility == Accessibility.Public
                    && prop.SetMethod != null
                    && prop.SetMethod.DeclaredAccessibility == Accessibility.Public
                    && !prop.IsStatic
                    && !prop.IsIndexer
                    && seen.Add(prop.Name))
                {
                    // Skip ManagerResponse base properties (handled separately)
                    if (prop.Name == "ActionId" || prop.Name == "Response"
                        || prop.Name == "Message" || prop.Name == "RawFields")
                        continue;

                    var propType = ClassifyPropertyType(prop.Type);
                    if (propType != PropertyType.Unsupported)
                    {
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
                "Asterisk.NetAot.Abstractions.Attributes.AsteriskMappingAttribute"
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

    private static string GenerateDeserializer(ImmutableArray<ResponseInfo> responses)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Frozen;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Asterisk.NetAot.Abstractions;");
        sb.AppendLine("using Asterisk.NetAot.Ami.Internal;");
        sb.AppendLine();
        sb.AppendLine("namespace Asterisk.NetAot.Ami.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// AOT-compatible response deserializer. Populates typed ManagerResponse instances");
        sb.AppendLine("/// from AmiMessage fields without reflection.");
        sb.AppendLine("/// Auto-generated by ResponseDeserializerGenerator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class GeneratedResponseDeserializer");
        sb.AppendLine("{");

        var sorted = responses.OrderBy(r => r.MappingName).ToArray();

        // Registry: action name -> factory
        sb.AppendLine("    private static readonly FrozenDictionary<string, Func<ManagerResponse>> Registry =");
        sb.AppendLine("        new Dictionary<string, Func<ManagerResponse>>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var resp in sorted)
        {
            sb.AppendLine($"            [\"{EscapeString(resp.MappingName)}\"] = static () => new {resp.FullyQualifiedTypeName}(),");
        }
        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();

        // Deserialize(msg, actionName)
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Deserializes an AmiMessage into a typed ManagerResponse based on the action name.");
        sb.AppendLine("    /// Returns a base ManagerResponse if the action name is not recognized.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static ManagerResponse Deserialize(AmiMessage msg, string actionName)");
        sb.AppendLine("    {");
        sb.AppendLine("        var resp = Registry.TryGetValue(actionName, out var factory)");
        sb.AppendLine("            ? factory()");
        sb.AppendLine("            : new ManagerResponse();");
        sb.AppendLine();
        sb.AppendLine("        // Set base ManagerResponse properties");
        sb.AppendLine("        resp.ActionId = msg.ActionId;");
        sb.AppendLine("        resp.Response = msg.ResponseStatus;");
        sb.AppendLine("        resp.Message = msg[\"Message\"];");
        sb.AppendLine("        resp.RawFields = msg.Fields;");
        sb.AppendLine();

        // Switch on concrete type for typed property assignment
        var responsesWithProps = sorted.Where(r => r.Properties.Length > 0).ToArray();
        if (responsesWithProps.Length > 0)
        {
            sb.AppendLine("        // Set typed response properties");
            sb.AppendLine("        switch (resp)");
            sb.AppendLine("        {");
            foreach (var resp in responsesWithProps)
            {
                sb.AppendLine($"            case {resp.FullyQualifiedTypeName} r:");
                sb.AppendLine("            {");
                foreach (var prop in resp.Properties)
                {
                    EmitPropertyDeserialization(sb, "r", prop, "                ");
                }
                sb.AppendLine("                break;");
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        return resp;");
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

    private static string ToLocalVar(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return "v";
        return char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
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

    private readonly struct ResponseInfo
    {
        public readonly string MappingName;
        public readonly string FullyQualifiedTypeName;
        public readonly string ClassName;
        public readonly PropertyInfo[] Properties;

        public ResponseInfo(string mappingName, string fullyQualifiedTypeName, string className, PropertyInfo[] properties)
        {
            MappingName = mappingName;
            FullyQualifiedTypeName = fullyQualifiedTypeName;
            ClassName = className;
            Properties = properties;
        }
    }
}
