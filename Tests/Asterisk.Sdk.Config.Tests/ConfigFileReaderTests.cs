using Asterisk.Sdk.Config;
using FluentAssertions;

namespace Asterisk.Sdk.Config.Tests;

public class ConfigFileReaderTests
{
    private static ConfigFile Parse(string content)
    {
        using var reader = new StringReader(content);
        return ConfigFileReader.Parse(reader, "test.conf");
    }

    [Fact]
    public void Parse_ShouldHandleSections()
    {
        var file = Parse("[general]\nkey=value\n\n[section2]\nfoo=bar\n");

        file.Categories.Should().HaveCount(2);
        file.Categories[0].Name.Should().Be("general");
        file.Categories[1].Name.Should().Be("section2");
    }

    [Fact]
    public void Parse_ShouldHandleKeyValuePairs()
    {
        var file = Parse("[general]\nhost = 192.168.1.1\nport=5060\n");

        var general = file.GetCategory("general");
        general.Should().NotBeNull();
        general!.Variables["host"].Should().Be("192.168.1.1");
        general.Variables["port"].Should().Be("5060");
    }

    [Fact]
    public void Parse_ShouldSkipComments()
    {
        var file = Parse("; This is a comment\n[general]\n; Another comment\nkey=value\n// C++ style\n");

        file.Categories.Should().HaveCount(1);
        file.Categories[0].Variables.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_ShouldStripInlineComments()
    {
        var file = Parse("[general]\nhost = 192.168.1.1 ; server address\n");

        file.GetCategory("general")!.Variables["host"].Should().Be("192.168.1.1");
    }

    [Fact]
    public void Parse_ShouldHandleTemplateInheritance()
    {
        var file = Parse("[2000](default-user)\ntype=friend\n");

        file.Categories[0].Name.Should().Be("2000");
        file.Categories[0].Template.Should().Be("default-user");
    }

    [Fact]
    public void Parse_ShouldHandleAppendOperator()
    {
        var file = Parse("[general]\nallow => ulaw\nallow => alaw\n");

        var vars = file.GetCategory("general")!.OrderedVariables;
        vars.Should().HaveCount(2);
        vars[0].IsAppend.Should().BeTrue();
        vars[0].Value.Should().Be("ulaw");
        vars[1].Value.Should().Be("alaw");
    }

    [Fact]
    public void Parse_ShouldHandleIncludeDirective()
    {
        var file = Parse("#include \"sip_users.conf\"\n[general]\nkey=val\n");

        file.Directives.Should().HaveCount(1);
        file.Directives[0].Should().BeOfType<IncludeDirective>();
        ((IncludeDirective)file.Directives[0]).Path.Should().Be("sip_users.conf");
    }

    [Fact]
    public void Parse_ShouldHandleExecDirective()
    {
        var file = Parse("#exec /usr/bin/generate-config.sh\n[general]\n");

        file.Directives.Should().HaveCount(1);
        file.Directives[0].Should().BeOfType<ExecDirective>();
    }

    [Fact]
    public void Parse_ShouldBeCaseInsensitiveForKeys()
    {
        var file = Parse("[general]\nHost=1.2.3.4\n");

        file.GetCategory("general")!.Variables["host"].Should().Be("1.2.3.4");
    }

    [Fact]
    public void Parse_RealWorldSipConf()
    {
        var content = """
            [general]
            context=default
            allowoverlap=no
            udpbindaddr=0.0.0.0
            tcpenable=no
            transport=udp

            [authentication]

            [2000](default-user)
            type=friend
            host=dynamic
            secret=password123
            context=from-internal
            allow => ulaw
            allow => alaw
            """;

        var file = Parse(content);

        file.Categories.Should().HaveCount(3);
        file.GetCategory("general")!.Variables["context"].Should().Be("default");
        file.GetCategory("2000")!.Variables["secret"].Should().Be("password123");
        file.GetCategory("2000")!.Template.Should().Be("default-user");
    }
}
