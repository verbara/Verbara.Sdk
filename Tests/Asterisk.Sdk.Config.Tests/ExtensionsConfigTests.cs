using Asterisk.Sdk.Config;
using FluentAssertions;

namespace Asterisk.Sdk.Config.Tests;

public class ExtensionsConfigTests
{
    private static ExtensionsConfigFile Parse(string content)
    {
        using var reader = new StringReader(content);
        return ExtensionsConfigFileReader.Parse(reader, "extensions.conf");
    }

    [Fact]
    public void Parse_ShouldExtractContexts()
    {
        var file = Parse("[from-internal]\nexten => 100,1,Answer()\n\n[from-external]\nexten => s,1,Hangup()\n");

        file.Contexts.Should().HaveCount(2);
        file.Contexts[0].Name.Should().Be("from-internal");
        file.Contexts[1].Name.Should().Be("from-external");
    }

    [Fact]
    public void Parse_ShouldExtractExtensions()
    {
        var content = "[from-internal]\nexten => 100,1,Answer()\nexten => 100,n,Playback(hello-world)\nexten => 100,n,Hangup()\n";
        var file = Parse(content);

        var ctx = file.GetContext("from-internal");
        ctx.Should().NotBeNull();
        ctx!.Extensions.Should().HaveCount(3);
        ctx.Extensions[0].Extension.Should().Be("100");
        ctx.Extensions[0].Priority.Should().Be(1);
        ctx.Extensions[0].Application.Should().Be("Answer()");
        ctx.Extensions[1].Priority.Should().Be(2);
        ctx.Extensions[2].Priority.Should().Be(3);
        ctx.Extensions[2].Application.Should().Be("Hangup()");
    }

    [Fact]
    public void Parse_ShouldHandleSameKeyword()
    {
        var content = "[default]\nexten => 200,1,Answer()\nsame => n,Wait(1)\nsame => n,Playback(demo-congrats)\nsame => n,Hangup()\n";
        var file = Parse(content);

        var extensions = file.GetContext("default")!.Extensions;
        extensions.Should().HaveCount(4);
        extensions[0].Extension.Should().Be("200");
        extensions[1].Extension.Should().Be("200");
        extensions[3].Priority.Should().Be(4);
    }

    [Fact]
    public void Parse_ShouldHandleIncludes()
    {
        var content = "[from-internal]\ninclude => default\ninclude => parkedcalls\nexten => 100,1,Answer()\n";
        var file = Parse(content);

        var ctx = file.GetContext("from-internal");
        ctx!.Includes.Should().Contain("default");
        ctx.Includes.Should().Contain("parkedcalls");
        ctx.Extensions.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_RealWorldDialplan()
    {
        var content = """
            [general]
            static=yes
            writeprotect=no

            [globals]
            CONSOLE => Console/dsp

            [default]
            include => parkedcalls
            exten => 100,1,Answer()
            same => n,Playback(hello-world)
            same => n,Hangup()

            exten => 200,1,Answer()
            same => n,Queue(sales,t)
            same => n,Hangup()
            """;

        var file = Parse(content);

        file.Contexts.Should().HaveCount(3);
        var defaultCtx = file.GetContext("default");
        defaultCtx.Should().NotBeNull();
        defaultCtx!.Includes.Should().Contain("parkedcalls");
        defaultCtx.Extensions.Should().HaveCount(6);

        var ext200 = defaultCtx.Extensions.Where(e => e.Extension == "200").ToList();
        ext200.Should().HaveCount(3);
        ext200[1].Application.Should().Be("Queue(sales,t)");
    }
}
