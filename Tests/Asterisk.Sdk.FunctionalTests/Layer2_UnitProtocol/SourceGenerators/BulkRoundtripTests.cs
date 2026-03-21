namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using Asterisk.Sdk.Attributes;
using FluentAssertions;

[Trait("Category", "Unit")]
[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Reflection is OK in test code")]
[SuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers", Justification = "Reflection is OK in test code")]
public sealed class BulkRoundtripTests
{
    /// <summary>
    /// Provides all event types discovered via reflection from the Ami assembly.
    /// Each entry is (mappingName, expectedType).
    /// </summary>
    public static TheoryData<string, Type> AllEventMappings
    {
        get
        {
            var data = new TheoryData<string, Type>();

            var amiAssembly = typeof(Asterisk.Sdk.Ami.Events.NewChannelEvent).Assembly;
            var eventTypes = amiAssembly.GetTypes()
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.IsAssignableTo(typeof(ManagerEvent))
                            && t.Namespace is not null
                            && t.Namespace.Contains("Events"))
                .ToList();

            foreach (var type in eventTypes)
            {
                var attr = type.GetCustomAttribute<AsteriskMappingAttribute>();
                if (attr is null)
                    continue;

                data.Add(attr.Name, type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllEventMappings))]
    public void AllEvents_ShouldDeserializeToCorrectType(string eventName, Type expectedType)
    {
        // Build a minimal AmiMessage with just the Event field
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventName,
        };
        var msg = new AmiMessage(fields);

        // Deserialize via the generated pipeline
        var evt = GeneratedEventDeserializer.Deserialize(msg);

        // The deserialized event should be the expected type (or a subclass, though events are sealed)
        evt.Should().BeOfType(expectedType,
            "event name '{0}' should deserialize to {1}", eventName, expectedType.Name);
    }

    /// <summary>
    /// Provides all action types discovered via reflection from the Ami assembly.
    /// Each entry is (mappingName, actionType).
    /// </summary>
    public static TheoryData<string, Type> AllActionMappings
    {
        get
        {
            var data = new TheoryData<string, Type>();

            var amiAssembly = typeof(Asterisk.Sdk.Ami.Actions.PingAction).Assembly;
            var actionTypes = amiAssembly.GetTypes()
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.IsAssignableTo(typeof(ManagerAction))
                            && t.Namespace is not null
                            && t.Namespace.Contains("Actions"))
                .ToList();

            foreach (var type in actionTypes)
            {
                var attr = type.GetCustomAttribute<AsteriskMappingAttribute>();
                if (attr is null)
                    continue;

                data.Add(attr.Name, type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllActionMappings))]
    public void AllActions_ShouldSerializeToCorrectName(string expectedName, Type actionType)
    {
        // Create an instance via reflection (OK in test code)
        var action = (ManagerAction)Activator.CreateInstance(actionType)!;

        // The generated serializer should return the correct AMI action name
        var actualName = GeneratedActionSerializer.GetActionName(action);
        actualName.Should().Be(expectedName,
            "action type {0} should serialize as '{1}'", actionType.Name, expectedName);
    }

    /// <summary>
    /// Provides all response types discovered via reflection from the Ami assembly.
    /// Each entry is (mappingName, responseType).
    /// </summary>
    public static TheoryData<string, Type> AllResponseMappings
    {
        get
        {
            var data = new TheoryData<string, Type>();

            var amiAssembly = typeof(Asterisk.Sdk.Ami.Responses.PingResponse).Assembly;
            var responseTypes = amiAssembly.GetTypes()
                .Where(t => t.IsClass
                            && !t.IsAbstract
                            && t.IsAssignableTo(typeof(ManagerResponse))
                            && t.Namespace is not null
                            && t.Namespace.Contains("Responses"))
                .ToList();

            foreach (var type in responseTypes)
            {
                var attr = type.GetCustomAttribute<AsteriskMappingAttribute>();
                if (attr is null)
                    continue;

                data.Add(attr.Name, type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllResponseMappings))]
    public void AllResponses_ShouldDeserializeToCorrectType(string actionName, Type expectedType)
    {
        // Build a minimal response AmiMessage
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Response"] = "Success",
        };
        var msg = new AmiMessage(fields);

        // Deserialize via the generated pipeline
        var response = GeneratedResponseDeserializer.Deserialize(msg, actionName);

        response.Should().BeOfType(expectedType,
            "action name '{0}' should produce response type {1}", actionName, expectedType.Name);
    }
}
