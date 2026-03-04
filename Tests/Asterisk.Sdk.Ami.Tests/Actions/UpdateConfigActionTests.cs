using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public class UpdateConfigActionTests
{
    [Fact]
    public void AddNewCategory_ShouldGenerateCorrectFields()
    {
        var action = new UpdateConfigAction
        {
            SrcFilename = "pjsip.conf",
            DstFilename = "pjsip.conf"
        };
        action.AddNewCategory("my-trunk");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(2);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "NewCat"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "my-trunk"));
    }

    [Fact]
    public void AddNewCategory_WithTemplate_ShouldIncludeVarField()
    {
        var action = new UpdateConfigAction();
        action.AddNewCategory("my-trunk", "base-template");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(3);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "NewCat"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "my-trunk"));
        fields[2].Should().Be(new KeyValuePair<string, string>("Var-000000", "base-template"));
    }

    [Fact]
    public void AddAppend_ShouldGenerateAllFourFields()
    {
        var action = new UpdateConfigAction();
        action.AddAppend("general", "transport", "udp");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(4);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "Append"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "general"));
        fields[2].Should().Be(new KeyValuePair<string, string>("Var-000000", "transport"));
        fields[3].Should().Be(new KeyValuePair<string, string>("Value-000000", "udp"));
    }

    [Fact]
    public void AddUpdate_ShouldGenerateAllFourFields()
    {
        var action = new UpdateConfigAction();
        action.AddUpdate("general", "host", "10.0.0.1");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(4);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "Update"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "general"));
        fields[2].Should().Be(new KeyValuePair<string, string>("Var-000000", "host"));
        fields[3].Should().Be(new KeyValuePair<string, string>("Value-000000", "10.0.0.1"));
    }

    [Fact]
    public void AddDelete_ShouldGenerateActionCatVar()
    {
        var action = new UpdateConfigAction();
        action.AddDelete("general", "context");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(3);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "Delete"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "general"));
        fields[2].Should().Be(new KeyValuePair<string, string>("Var-000000", "context"));
    }

    [Fact]
    public void AddDeleteCategory_ShouldGenerateActionCatOnly()
    {
        var action = new UpdateConfigAction();
        action.AddDeleteCategory("old-trunk");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(2);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "DelCat"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "old-trunk"));
    }

    [Fact]
    public void AddRenameCat_ShouldGenerateActionCatVar()
    {
        var action = new UpdateConfigAction();
        action.AddRenameCat("old-name", "new-name");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(3);
        fields[0].Should().Be(new KeyValuePair<string, string>("Action-000000", "RenameCat"));
        fields[1].Should().Be(new KeyValuePair<string, string>("Cat-000000", "old-name"));
        fields[2].Should().Be(new KeyValuePair<string, string>("Var-000000", "new-name"));
    }

    [Fact]
    public void MultipleOperations_ShouldNumberSequentially()
    {
        var action = new UpdateConfigAction
        {
            SrcFilename = "pjsip.conf",
            DstFilename = "pjsip.conf"
        };
        action
            .AddNewCategory("my-trunk")
            .AddAppend("my-trunk", "type", "endpoint")
            .AddAppend("my-trunk", "transport", "transport-udp")
            .AddAppend("my-trunk", "context", "from-trunk");

        var fields = action.GetExtraFields().ToList();

        // Operation 0: NewCat (2 fields)
        fields[0].Key.Should().Be("Action-000000");
        fields[0].Value.Should().Be("NewCat");
        fields[1].Key.Should().Be("Cat-000000");

        // Operation 1: Append (4 fields)
        fields[2].Key.Should().Be("Action-000001");
        fields[2].Value.Should().Be("Append");
        fields[3].Key.Should().Be("Cat-000001");
        fields[4].Key.Should().Be("Var-000001");
        fields[4].Value.Should().Be("type");
        fields[5].Key.Should().Be("Value-000001");
        fields[5].Value.Should().Be("endpoint");

        // Operation 2: Append (4 fields)
        fields[6].Key.Should().Be("Action-000002");
        fields[7].Key.Should().Be("Cat-000002");
        fields[8].Key.Should().Be("Var-000002");
        fields[8].Value.Should().Be("transport");

        // Operation 3: Append (4 fields)
        fields[10].Key.Should().Be("Action-000003");
    }

    [Fact]
    public void FluentBuilder_ShouldReturnSameInstance()
    {
        var action = new UpdateConfigAction();

        var result = action
            .AddNewCategory("test")
            .AddAppend("test", "key", "value")
            .AddUpdate("test", "key", "value2")
            .AddDelete("test", "key")
            .AddDeleteCategory("test")
            .AddRenameCat("a", "b");

        result.Should().BeSameAs(action);
    }

    [Fact]
    public void ImplementsIHasExtraFields()
    {
        var action = new UpdateConfigAction();
        action.Should().BeAssignableTo<IHasExtraFields>();
    }

    [Fact]
    public void NoOperations_ShouldReturnEmptyExtraFields()
    {
        var action = new UpdateConfigAction();
        action.GetExtraFields().Should().BeEmpty();
    }
}
