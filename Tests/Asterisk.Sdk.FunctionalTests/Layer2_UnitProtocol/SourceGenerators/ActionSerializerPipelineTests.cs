namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Generated;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class ActionSerializerPipelineTests
{
    [Fact]
    public void Serialize_PingAction_ShouldProduceNoFields()
    {
        var action = new PingAction();

        var fields = GeneratedActionSerializer.Serialize(action).ToList();

        fields.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_OriginateAction_ShouldIncludeAllSetProperties()
    {
        var action = new OriginateAction
        {
            Channel = "SIP/peer-0001",
            Application = "Playback",
            Data = "hello-world",
            IsAsync = true,
        };

        var fields = GeneratedActionSerializer.Serialize(action).ToList();

        fields.Should().Contain(kvp => kvp.Key == "Channel" && kvp.Value == "SIP/peer-0001");
        fields.Should().Contain(kvp => kvp.Key == "Application" && kvp.Value == "Playback");
        fields.Should().Contain(kvp => kvp.Key == "Data" && kvp.Value == "hello-world");
        fields.Should().Contain(kvp => kvp.Key == "Async" && kvp.Value == "true");
    }

    [Fact]
    public void Serialize_ShouldOmitNullFields()
    {
        var action = new OriginateAction
        {
            Channel = "SIP/peer-0001",
        };

        var fields = GeneratedActionSerializer.Serialize(action).ToList();

        fields.Should().ContainSingle(kvp => kvp.Key == "Channel");
        fields.Should().NotContain(kvp => kvp.Key == "Application");
        fields.Should().NotContain(kvp => kvp.Key == "Context");
        fields.Should().NotContain(kvp => kvp.Key == "Async");
        fields.Should().NotContain(kvp => kvp.Key == "Data");
    }

    [Fact]
    public void Serialize_ShouldDistinguishEmptyStringFromNull()
    {
        var withEmpty = new OriginateAction { Channel = "" };
        var withNull = new OriginateAction { Channel = null };

        var emptyFields = GeneratedActionSerializer.Serialize(withEmpty).ToList();
        var nullFields = GeneratedActionSerializer.Serialize(withNull).ToList();

        emptyFields.Should().Contain(kvp => kvp.Key == "Channel" && kvp.Value == "");
        nullFields.Should().NotContain(kvp => kvp.Key == "Channel");
    }

    [Fact]
    public void Serialize_ShouldIncludeExtraFields_ForIHasExtraFields()
    {
        var action = new UpdateConfigAction
        {
            SrcFilename = "sip.conf",
            DstFilename = "sip.conf",
        };
        action.AddAppend("general", "allow", "ulaw");

        var fields = GeneratedActionSerializer.Serialize(action).ToList();

        fields.Should().Contain(kvp => kvp.Key == "SrcFilename" && kvp.Value == "sip.conf");
        fields.Should().Contain(kvp => kvp.Key == "Action-000000" && kvp.Value == "Append");
        fields.Should().Contain(kvp => kvp.Key == "Cat-000000" && kvp.Value == "general");
        fields.Should().Contain(kvp => kvp.Key == "Var-000000" && kvp.Value == "allow");
        fields.Should().Contain(kvp => kvp.Key == "Value-000000" && kvp.Value == "ulaw");
    }

    [Fact]
    public void GetActionName_ShouldReturnRegisteredName()
    {
        GeneratedActionSerializer.GetActionName(new PingAction()).Should().Be("Ping");
        GeneratedActionSerializer.GetActionName(new OriginateAction()).Should().Be("Originate");
        GeneratedActionSerializer.GetActionName(new CommandAction()).Should().Be("Command");
    }
}
