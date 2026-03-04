using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using DashboardExample.Services;
using FluentAssertions;

namespace DashboardExample.Tests.Services;

public class PbxConfigManagerTests
{
    [Fact]
    public void UpdateConfigAction_ShouldBeUsedForCreateSection()
    {
        // Verify that UpdateConfigAction builder produces correct fields for section creation
        var action = new UpdateConfigAction
        {
            SrcFilename = "pjsip.conf",
            DstFilename = "pjsip.conf",
        };

        action.AddNewCategory("my-trunk");
        action.AddAppend("my-trunk", "type", "endpoint");
        action.AddAppend("my-trunk", "context", "from-trunk");

        var fields = action.GetExtraFields().ToList();

        // NewCat operation: Action-000000 + Cat-000000
        fields[0].Key.Should().Be("Action-000000");
        fields[0].Value.Should().Be("NewCat");
        fields[1].Key.Should().Be("Cat-000000");
        fields[1].Value.Should().Be("my-trunk");

        // First Append: Action-000001 + Cat-000001 + Var-000001 + Value-000001
        fields[2].Key.Should().Be("Action-000001");
        fields[2].Value.Should().Be("Append");
        fields[4].Key.Should().Be("Var-000001");
        fields[4].Value.Should().Be("type");
        fields[5].Key.Should().Be("Value-000001");
        fields[5].Value.Should().Be("endpoint");

        // Second Append
        fields[8].Key.Should().Be("Var-000002");
        fields[8].Value.Should().Be("context");
    }

    [Fact]
    public void UpdateConfigAction_ShouldHandleDeleteSection()
    {
        var action = new UpdateConfigAction
        {
            SrcFilename = "pjsip.conf",
            DstFilename = "pjsip.conf",
        };
        action.AddDeleteCategory("my-trunk");

        var fields = action.GetExtraFields().ToList();

        fields.Should().HaveCount(2);
        fields[0].Value.Should().Be("DelCat");
        fields[1].Value.Should().Be("my-trunk");
    }

    [Fact]
    public void UpdateConfigAction_ShouldHandleUpdateSection()
    {
        // Update = delete + recreate
        var action = new UpdateConfigAction
        {
            SrcFilename = "sip.conf",
            DstFilename = "sip.conf",
        };

        action.AddDeleteCategory("my-trunk");
        action.AddNewCategory("my-trunk");
        action.AddAppend("my-trunk", "type", "peer");

        var fields = action.GetExtraFields().ToList();

        // 3 operations: DelCat(2) + NewCat(2) + Append(4) = 8 fields
        fields.Should().HaveCount(8);
        fields[0].Value.Should().Be("DelCat");
        fields[2].Value.Should().Be("NewCat");
        fields[4].Value.Should().Be("Append");
    }

    [Fact]
    public void GetConfigResponse_ShouldParseCategoriesCorrectly()
    {
        var response = new GetConfigResponse
        {
            Response = "Success",
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "my-trunk",
                ["Line-000000-000000"] = "type=endpoint",
                ["Line-000000-000001"] = "context=from-trunk",
                ["Category-000001"] = "my-trunk-auth",
                ["Line-000001-000000"] = "type=auth",
                ["Line-000001-000001"] = "username=user1",
            }
        };

        response.Categories.Should().HaveCount(2);
        response.Categories[0].Name.Should().Be("my-trunk");
        response.Categories[0].Variables["type"].Should().Be("endpoint");
        response.Categories[0].Variables["context"].Should().Be("from-trunk");
        response.Categories[1].Name.Should().Be("my-trunk-auth");
        response.Categories[1].Variables["username"].Should().Be("user1");
    }

    [Fact]
    public void CommandResponse_ShouldExtractOutput()
    {
        var response = new CommandResponse
        {
            Response = "Success",
            RawFields = new Dictionary<string, string>
            {
                ["__CommandOutput"] = "Module loaded successfully"
            }
        };

        response.Output.Should().Be("Module loaded successfully");
    }
}
