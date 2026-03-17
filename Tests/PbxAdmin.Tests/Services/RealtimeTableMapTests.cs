using PbxAdmin.Services;
using FluentAssertions;

namespace PbxAdmin.Tests.Services;

public class RealtimeTableMapTests
{
    [Theory]
    [InlineData("pjsip.conf", 4)]
    [InlineData("sip.conf", 1)]
    [InlineData("iax.conf", 1)]
    [InlineData("queues.conf", 2)]
    [InlineData("voicemail.conf", 1)]
    [InlineData("unknown.conf", 0)]
    public void GetTables_ShouldReturnCorrectCount(string filename, int expected)
    {
        RealtimeTableMap.GetTables(filename).Should().HaveCount(expected);
    }

    [Theory]
    [InlineData("PJSIP.CONF", 4)]
    [InlineData("Pjsip.Conf", 4)]
    [InlineData("SIP.CONF", 1)]
    public void GetTables_ShouldBeCaseInsensitive(string filename, int expected)
    {
        RealtimeTableMap.GetTables(filename).Should().HaveCount(expected);
    }

    [Fact]
    public void GetTables_PjsipTables_ShouldHaveCorrectTypeValues()
    {
        var tables = RealtimeTableMap.GetTables("pjsip.conf");

        tables[0].TableName.Should().Be("ps_endpoints");
        tables[0].TypeValue.Should().Be("endpoint");

        tables[1].TableName.Should().Be("ps_auths");
        tables[1].TypeValue.Should().Be("auth");

        tables[2].TableName.Should().Be("ps_aors");
        tables[2].TypeValue.Should().Be("aor");

        tables[3].TableName.Should().Be("ps_registrations");
        tables[3].TypeValue.Should().Be("registration");
    }

    [Fact]
    public void GetTables_PjsipTables_ShouldUseIdColumn()
    {
        var tables = RealtimeTableMap.GetTables("pjsip.conf");
        tables.Should().AllSatisfy(t => t.IdColumn.Should().Be("id"));
    }

    [Fact]
    public void GetTables_SipTable_ShouldUseNameColumn()
    {
        var tables = RealtimeTableMap.GetTables("sip.conf");
        tables[0].IdColumn.Should().Be("name");
    }

    [Fact]
    public void GetTables_QueuesTables_ShouldHaveNoTypeValue()
    {
        var tables = RealtimeTableMap.GetTables("queues.conf");
        tables.Should().AllSatisfy(t => t.TypeValue.Should().BeNull());
    }

    [Fact]
    public void ResolveTable_ShouldMatchByTypeVariable()
    {
        var tables = RealtimeTableMap.GetTables("pjsip.conf");
        var variables = new Dictionary<string, string> { ["type"] = "auth" };

        var result = RealtimeTableMap.ResolveTable(tables, variables);

        result.Should().NotBeNull();
        result!.TableName.Should().Be("ps_auths");
    }

    [Fact]
    public void ResolveTable_ShouldFallbackToFirstTable_WhenNoTypeMatch()
    {
        var tables = RealtimeTableMap.GetTables("sip.conf");
        var variables = new Dictionary<string, string> { ["host"] = "example.com" };

        var result = RealtimeTableMap.ResolveTable(tables, variables);

        result.Should().NotBeNull();
        result!.TableName.Should().Be("sippeers");
    }

    [Fact]
    public void ResolveTable_ShouldReturnNull_WhenNoTables()
    {
        var tables = RealtimeTableMap.GetTables("unknown.conf");
        var variables = new Dictionary<string, string>();

        RealtimeTableMap.ResolveTable(tables, variables).Should().BeNull();
    }
}
