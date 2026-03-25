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

    [Fact]
    public void Categories_ShouldSkipCategoryKey_WhenExactlyPrefixWithNoNumber()
    {
        // "Category-" is length 9, so key.Length > 9 must reject it
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-"] = "should-be-ignored",
                ["Category-000000"] = "valid",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Name.Should().Be("valid");
    }

    [Fact]
    public void Categories_ShouldReturnEmpty_WhenOnlyCategoryPrefixKeyExists()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-"] = "should-be-ignored",
            }
        };

        response.Categories.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldSkipLineKey_WhenExactlyPrefixWithNoContent()
    {
        // "Line-" is length 5, so key.Length <= 5 must reject it
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-"] = "type=endpoint",
                ["Line-000000-000000"] = "context=default",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().HaveCount(1);
        categories[0].Variables["context"].Should().Be("default");
    }

    [Fact]
    public void Categories_ShouldSkipLine_WhenNoDashAfterCategoryNumber()
    {
        // rest = "000000" with no dash -> dashIdx == -1
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000"] = "type=endpoint",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldSkipLine_WhenDashIsAtPositionZero()
    {
        // rest = "-000000" -> dashIdx == 0, catNum would be empty
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line--000000"] = "type=endpoint",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldSkipLine_WhenCategoryNumberDoesNotExist()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-999999-000000"] = "type=orphan",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldSkipLine_WhenValueStartsWithEquals()
    {
        // eqIdx == 0, which means varName would be empty -> skip
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "=nokey",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldTrimWhitespace_AroundKeyAndValue()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "  key  =  value  ",
            }
        };

        var categories = response.Categories;

        categories[0].Variables["key"].Should().Be("value");
    }

    [Fact]
    public void Categories_ShouldSortCategoriesByNumber()
    {
        // SortedDictionary orders by key, so categories should appear in numeric order
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000002"] = "third",
                ["Category-000000"] = "first",
                ["Category-000001"] = "second",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(3);
        categories[0].Name.Should().Be("first");
        categories[1].Name.Should().Be("second");
        categories[2].Name.Should().Be("third");
    }

    [Fact]
    public void Categories_ShouldHandleCategoryWithNoLines()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "empty-section",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Name.Should().Be("empty-section");
        categories[0].Variables.Should().BeEmpty();
    }

    [Fact]
    public void Categories_ShouldHandleNonLineNonCategoryFields()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Response"] = "Success",
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "type=endpoint",
                ["ActionID"] = "abc123",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(1);
        categories[0].Variables.Should().HaveCount(1);
    }

    [Fact]
    public void Categories_ShouldBeCaseInsensitive_ForCategoryPrefix()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["category-000000"] = "lower",
                ["CATEGORY-000001"] = "upper",
            }
        };

        var categories = response.Categories;

        categories.Should().HaveCount(2);
    }

    [Fact]
    public void Categories_ShouldBeCaseInsensitive_ForLinePrefix()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["line-000000-000000"] = "type=endpoint",
            }
        };

        var categories = response.Categories;

        categories[0].Variables.Should().HaveCount(1);
        categories[0].Variables["type"].Should().Be("endpoint");
    }

    [Fact]
    public void Categories_ShouldHandleEmptyValue_AfterEquals()
    {
        var response = new GetConfigResponse
        {
            RawFields = new Dictionary<string, string>
            {
                ["Category-000000"] = "general",
                ["Line-000000-000000"] = "key=",
            }
        };

        var categories = response.Categories;

        categories[0].Variables.Should().HaveCount(1);
        categories[0].Variables["key"].Should().BeEmpty();
    }
}
