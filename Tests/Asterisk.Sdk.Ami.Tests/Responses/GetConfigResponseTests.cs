using Asterisk.Sdk.Ami.Responses;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Responses;

public class GetConfigResponseTests
{
    [Fact]
    public void Categories_ShouldParseFromRawFields()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "type=endpoint",
                ["Line-000000-000001"] = "transport=transport-udp",
                ["Line-000000-000002"] = "context=from-trunk",
                ["Category-000001"] = "my-trunk-auth",
                ["Line-000001-000000"] = "type=auth",
                ["Line-000001-000001"] = "username=myuser",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(2);

        categories[0].Name.Should().Be("general");
        categories[0].Variables.Should().HaveCount(3);
        categories[0].Variables["type"].Should().Be("endpoint");
        categories[0].Variables["transport"].Should().Be("transport-udp");
        categories[0].Variables["context"].Should().Be("from-trunk");

        categories[1].Name.Should().Be("my-trunk-auth");
        categories[1].Variables.Should().HaveCount(2);
        categories[1].Variables["type"].Should().Be("auth");
        categories[1].Variables["username"].Should().Be("myuser");
    }

    [Fact]
    public void Categories_ShouldReturnEmpty_WhenRawFieldsNull()
    {
        var response = new GetConfigResponse { RawFields = null };
        response.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldReturnEmpty_WhenRawFieldsEmpty()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>()
        };
        response.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldReturnEmpty_WhenNoCategoryFields()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Response"] = "Success",
                ["Message"] = "ok"
            }
        };
        response.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldSkipMalformedLines()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "type=endpoint",
                ["Line-000000-000001"] = "malformed-no-equals",
                ["Line-000000-000002"] = "context=from-trunk",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().HaveCount(2);
        categories[0].Variables.Should().ContainKey("type");
        categories[0].Variables.Should().ContainKey("context");
        categories[0].Variables.Should().NotContainKey("malformed-no-equals");
    }

    [Fact]
    public void Categories_ShouldHandleValuesWithEquals()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "password=abc=123==",
            }
        };

        var categories = response.Categories;

        categories[0].Variables["password"].Should().Be("abc=123==");
    }

    [Fact]
    public void Categories_ShouldBeCached()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "test",
            }
        };

        var first = response.Categories;
        var second = response.Categories;
        first.Should().BeSameAs(second);
    }
}
