using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Attributes;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Ami.Tests.Events;

/// <summary>
/// Every <c>bool?</c> member of every mapped AMI event and response reads Asterisk's true/false vocabulary,
/// through the generated deserializers, without the member being named here. A member added later, or one
/// inherited from an intermediate base class, is covered by the same sweep.
/// </summary>
/// <remarks>
/// The types and members are found by reflection over the Ami assembly, so no obsolete type is named in this
/// file. Each member gets a message carrying its event or action name and its own header (the property's
/// <see cref="VerbaraMappingAttribute"/>, else its name), once per token and once with an unknown value.
/// </remarks>
public sealed class BooleanHeaderSweepTests(ITestOutputHelper output)
{
    // Asterisk's ast_true()/ast_false() words, chan_dahdi's enabled/disabled, and one value outside the set.
    private static readonly (string Token, bool? Expected)[] Readings =
    [
        ("yes", true), ("true", true), ("y", true), ("t", true), ("1", true), ("on", true), ("enabled", true),
        ("no", false), ("false", false), ("n", false), ("f", false), ("0", false), ("off", false), ("disabled", false),
        ("maybe", null),
    ];

    [Fact]
    public void Deserialize_ShouldReadAsteriskVocabulary_WhenAnyMappedTypeCarriesABooleanMember()
    {
        var members = EnumerateBooleanMembers();
        var pairs = members.Count;
        var declarations = members
            .Select(static m => (m.Property.DeclaringType, m.Property.Name))
            .Distinct()
            .Count();
        var counts = $"{pairs} (type, property) pairs and {declarations} distinct declarations";
        output.WriteLine($"Boolean sweep checked {counts}.");

        pairs.Should().BeGreaterThan(0,
            $"the sweep must check at least one bool? member, and it found {counts}");

        var mismatches = new List<string>();
        foreach (var member in members)
        {
            foreach (var (token, expected) in Readings)
            {
                var read = ReadBack(member, token, out var created);
                if (created != member.Type)
                {
                    mismatches.Add($"{member.Type.Name}.{member.Property.Name}: \"{member.MappingName}\" created {created.Name}");
                    break;
                }

                if (read != expected)
                {
                    mismatches.Add(
                        $"{member.Type.Name}.{member.Property.Name} (header {member.Header}): \"{token}\" read {Show(read)}, expected {Show(expected)}");
                }
            }
        }

        foreach (var mismatch in mismatches)
            output.WriteLine(mismatch);

        mismatches.Should().BeEmpty(
            $"every bool? member reads Asterisk's vocabulary; the sweep checked {counts} and found {mismatches.Count} mismatches");
    }

    private static bool? ReadBack(BooleanMember member, string token, out Type created)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [member.Header] = token,
        };

        object result;
        if (member.IsEvent)
        {
            fields["Event"] = member.MappingName;
            result = GeneratedEventDeserializer.Deserialize(new AmiMessage(fields));
        }
        else
        {
            fields["Response"] = "Success";
            result = GeneratedResponseDeserializer.Deserialize(new AmiMessage(fields), member.MappingName);
        }

        created = result.GetType();
        return created == member.Type ? (bool?)member.Property.GetValue(result) : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: enumerates the Ami assembly's own types, never trimmed here.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test code: reads the public properties of the Ami assembly's own types, never trimmed here.")]
    private static List<BooleanMember> EnumerateBooleanMembers()
    {
        var mappedTypes = typeof(AmiBoolean).Assembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract
                && (type.IsSubclassOf(typeof(ManagerEvent)) || type.IsSubclassOf(typeof(ManagerResponse))))
            .Select(static type => (Type: type, MappingName: type.GetCustomAttribute<VerbaraMappingAttribute>(inherit: false)?.Name))
            .Where(static mapped => !string.IsNullOrEmpty(mapped.MappingName));

        var members = new List<BooleanMember>();
        foreach (var (type, mappingName) in mappedTypes)
        {
            var isEvent = type.IsSubclassOf(typeof(ManagerEvent));
            var booleanProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.PropertyType == typeof(bool?)
                    && property.GetIndexParameters().Length == 0
                    && property.GetMethod is { IsPublic: true }
                    && property.SetMethod is { IsPublic: true });

            foreach (var property in booleanProperties)
            {
                var header = property.GetCustomAttribute<VerbaraMappingAttribute>(inherit: false)?.Name;
                members.Add(new BooleanMember(
                    type,
                    mappingName!,
                    isEvent,
                    property,
                    string.IsNullOrEmpty(header) ? property.Name : header));
            }
        }

        return members;
    }

    private static string Show(bool? value) => value?.ToString() ?? "null";

    private sealed record BooleanMember(
        Type Type,
        string MappingName,
        bool IsEvent,
        PropertyInfo Property,
        string Header);
}
