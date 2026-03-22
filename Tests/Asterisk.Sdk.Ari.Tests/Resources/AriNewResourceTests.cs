using System.Net;
using System.Text.Json;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

public class AriNewResourceTests
{
    private static readonly AriClientOptions DefaultOptions = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp"
    };

    private static HttpClient CreateHttpClient(FakeHttpHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8088/ari/")
        };
        return client;
    }

    // --- ARI Asterisk Resource ---

    [Fact]
    public async Task Asterisk_GetInfoAsync_ShouldReturnInfo()
    {
        var json = JsonSerializer.Serialize(
            new AriAsteriskInfo
            {
                Build = new AriBuildInfo { Os = "Linux", Kernel = "5.15", Machine = "x86_64", Options = "", Date = "2026-01-01", User = "root" },
                System = new AriSystemInfo { Version = "22.0.0", EntityId = "server-1" },
                Config = new AriConfigInfo { Name = "asterisk", DefaultLanguage = "en" },
                Status = new AriStatusInfo { StartupTime = "2026-01-01T00:00:00Z", LastReloadTime = "2026-01-01T01:00:00Z" }
            },
            AriJsonContext.Default.AriAsteriskInfo);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.GetInfoAsync();

        result.Should().NotBeNull();
        result.Build.Should().NotBeNull();
        result.Build!.Os.Should().Be("Linux");
        result.System.Should().NotBeNull();
        result.System!.Version.Should().Be("22.0.0");
        result.Config.Should().NotBeNull();
        result.Config!.Name.Should().Be("asterisk");
        result.Status.Should().NotBeNull();
        result.Status!.StartupTime.Should().Be("2026-01-01T00:00:00Z");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/info");
    }

    [Fact]
    public async Task Asterisk_PingAsync_ShouldReturnPing()
    {
        var json = JsonSerializer.Serialize(
            new AriAsteriskPing { AsteriskId = "srv-1", Ping = "pong", Timestamp = "2026-01-01T00:00:00Z" },
            AriJsonContext.Default.AriAsteriskPing);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.PingAsync();

        result.Should().NotBeNull();
        result.AsteriskId.Should().Be("srv-1");
        result.Ping.Should().Be("pong");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/ping");
    }

    [Fact]
    public async Task Asterisk_GetVariableAsync_ShouldReturnValue()
    {
        var json = JsonSerializer.Serialize(
            new AriVariable { Value = "test" },
            AriJsonContext.Default.AriVariable);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.GetVariableAsync("FOO");

        result.Should().Be("test");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/variable");
        handler.LastRequestUri.Should().Contain("variable=FOO");
    }

    [Fact]
    public async Task Asterisk_SetVariableAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.SetVariableAsync("MY_VAR", "hello");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("asterisk/variable");
        handler.LastRequestUri.Should().Contain("variable=MY_VAR");
        handler.LastRequestUri.Should().Contain("value=hello");
    }

    [Fact]
    public async Task Asterisk_ListModulesAsync_ShouldReturnModules()
    {
        var json = JsonSerializer.Serialize(
            new[]
            {
                new AriModule { Name = "res_pjsip", Description = "PJSIP stack", UseCount = 3, Status = "Running", SupportLevel = "core" },
                new AriModule { Name = "chan_sip", Description = "SIP channel", UseCount = 0, Status = "Not Running", SupportLevel = "deprecated" }
            },
            AriJsonContext.Default.AriModuleArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.ListModulesAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("res_pjsip");
        result[1].Name.Should().Be("chan_sip");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/modules");
    }

    [Fact]
    public async Task Asterisk_GetModuleAsync_ShouldReturnModule()
    {
        var json = JsonSerializer.Serialize(
            new AriModule { Name = "res_pjsip", Description = "PJSIP stack", UseCount = 5, Status = "Running", SupportLevel = "core" },
            AriJsonContext.Default.AriModule);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.GetModuleAsync("res_pjsip");

        result.Should().NotBeNull();
        result.Name.Should().Be("res_pjsip");
        result.UseCount.Should().Be(5);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/modules/res_pjsip");
    }

    [Fact]
    public async Task Asterisk_LoadModuleAsync_ShouldPost()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.LoadModuleAsync("res_pjsip");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("asterisk/modules/res_pjsip");
    }

    [Fact]
    public async Task Asterisk_UnloadModuleAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.UnloadModuleAsync("chan_sip");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("asterisk/modules/chan_sip");
    }

    [Fact]
    public async Task Asterisk_ReloadModuleAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.ReloadModuleAsync("res_pjsip");

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("asterisk/modules/res_pjsip");
    }

    [Fact]
    public async Task Asterisk_ListLoggingAsync_ShouldReturnLogChannels()
    {
        var json = JsonSerializer.Serialize(
            new[]
            {
                new AriLogChannel { Channel = "console", Type = "file", Status = "Enabled", Configuration = "notice,warning,error" },
                new AriLogChannel { Channel = "messages", Type = "file", Status = "Enabled", Configuration = "notice,warning,error,verbose" }
            },
            AriJsonContext.Default.AriLogChannelArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        var result = await sut.ListLoggingAsync();

        result.Should().HaveCount(2);
        result[0].Channel.Should().Be("console");
        result[1].Channel.Should().Be("messages");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("asterisk/logging");
    }

    [Fact]
    public async Task Asterisk_RotateLogChannelAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriAsteriskResource(http);

        await sut.RotateLogChannelAsync("messages");

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Contain("asterisk/logging/messages/rotate");
    }

    // --- ARI Mailboxes Resource ---

    [Fact]
    public async Task Mailboxes_ListAsync_ShouldReturnMailboxes()
    {
        var json = JsonSerializer.Serialize(
            new[]
            {
                new AriMailbox { Name = "1000", OldMessages = 2, NewMessages = 1 },
                new AriMailbox { Name = "2000", OldMessages = 0, NewMessages = 3 }
            },
            AriJsonContext.Default.AriMailboxArray);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriMailboxesResource(http);

        var result = await sut.ListAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("1000");
        result[0].OldMessages.Should().Be(2);
        result[0].NewMessages.Should().Be(1);
        result[1].Name.Should().Be("2000");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("mailboxes");
    }

    [Fact]
    public async Task Mailboxes_GetAsync_ShouldReturnMailbox()
    {
        var json = JsonSerializer.Serialize(
            new AriMailbox { Name = "1000", OldMessages = 5, NewMessages = 2 },
            AriJsonContext.Default.AriMailbox);
        using var handler = new FakeHttpHandler(json);
        using var http = CreateHttpClient(handler);
        var sut = new AriMailboxesResource(http);

        var result = await sut.GetAsync("1000");

        result.Name.Should().Be("1000");
        result.OldMessages.Should().Be(5);
        result.NewMessages.Should().Be(2);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().Contain("mailboxes/1000");
    }

    [Fact]
    public async Task Mailboxes_UpdateAsync_ShouldSendPut()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriMailboxesResource(http);

        await sut.UpdateAsync("1000", oldMessages: 3, newMessages: 1);

        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastRequestUri.Should().Contain("mailboxes/1000");
        handler.LastRequestUri.Should().Contain("oldMessages=3");
        handler.LastRequestUri.Should().Contain("newMessages=1");
    }

    [Fact]
    public async Task Mailboxes_DeleteAsync_ShouldSendDelete()
    {
        using var handler = new FakeHttpHandler("");
        using var http = CreateHttpClient(handler);
        var sut = new AriMailboxesResource(http);

        await sut.DeleteAsync("1000");

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Contain("mailboxes/1000");
    }

    // --- New Model Serialization (JSON round-trip) ---

    [Fact]
    public void AriAsteriskInfo_ShouldDeserializeFromJson()
    {
        const string json = """
        {
            "build": { "os": "Linux", "kernel": "5.15.0", "machine": "x86_64", "options": "OPTIONAL_API", "date": "2026-01-01", "user": "asterisk" },
            "system": { "version": "22.0.0", "entity_id": "server-1" },
            "config": { "name": "My PBX", "default_language": "en", "max_channels": 100.0 },
            "status": { "startup_time": "2026-01-01T00:00:00Z", "last_reload_time": "2026-01-02T00:00:00Z" }
        }
        """;

        var info = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriAsteriskInfo);

        info.Should().NotBeNull();
        info!.Build.Should().NotBeNull();
        info.Build!.Os.Should().Be("Linux");
        info.Build.Kernel.Should().Be("5.15.0");
        info.Build.Machine.Should().Be("x86_64");
        info.System.Should().NotBeNull();
        info.System!.Version.Should().Be("22.0.0");
        info.System.EntityId.Should().Be("server-1");
        info.Config.Should().NotBeNull();
        info.Config!.Name.Should().Be("My PBX");
        info.Config.DefaultLanguage.Should().Be("en");
        info.Config.MaxChannels.Should().Be(100.0);
        info.Status.Should().NotBeNull();
        info.Status!.StartupTime.Should().Be("2026-01-01T00:00:00Z");
    }

    [Fact]
    public void AriModule_ShouldDeserializeFromJson()
    {
        const string json = """{"name":"res_pjsip","description":"PJSIP module","use_count":5,"status":"Running","support_level":"core"}""";

        var module = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriModule);

        module.Should().NotBeNull();
        module!.Name.Should().Be("res_pjsip");
        module.Description.Should().Be("PJSIP module");
        module.UseCount.Should().Be(5);
        module.Status.Should().Be("Running");
        module.SupportLevel.Should().Be("core");
    }

    [Fact]
    public void AriLogChannel_ShouldDeserializeFromJson()
    {
        const string json = """{"channel":"console","type":"file","status":"Enabled","configuration":"notice,warning,error"}""";

        var logChannel = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriLogChannel);

        logChannel.Should().NotBeNull();
        logChannel!.Channel.Should().Be("console");
        logChannel.Type.Should().Be("file");
        logChannel.Status.Should().Be("Enabled");
        logChannel.Configuration.Should().Be("notice,warning,error");
    }

    [Fact]
    public void AriMailbox_ShouldDeserializeFromJson()
    {
        const string json = """{"name":"1000","old_messages":4,"new_messages":2}""";

        var mailbox = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriMailbox);

        mailbox.Should().NotBeNull();
        mailbox!.Name.Should().Be("1000");
        mailbox.OldMessages.Should().Be(4);
        mailbox.NewMessages.Should().Be(2);
    }

    [Fact]
    public void AriRtpStats_ShouldDeserializeFromJson()
    {
        const string json = """
        {
            "txcount": 1000,
            "rxcount": 980,
            "txjitter": 1.5,
            "rxjitter": 2.0,
            "txploss": 5,
            "rxploss": 3,
            "rtt": 42,
            "channel_uniqueid": "1234567890.1",
            "local_ssrc": 111111,
            "remote_ssrc": 222222
        }
        """;

        var stats = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriRtpStats);

        stats.Should().NotBeNull();
        stats!.Txcount.Should().Be(1000);
        stats.Rxcount.Should().Be(980);
        stats.Txjitter.Should().Be(1.5);
        stats.Rxjitter.Should().Be(2.0);
        stats.Txploss.Should().Be(5);
        stats.Rxploss.Should().Be(3);
        stats.Rtt.Should().Be(42);
        stats.ChannelUniqueid.Should().Be("1234567890.1");
        stats.LocalSsrc.Should().Be(111111);
        stats.RemoteSsrc.Should().Be(222222);
    }

    [Fact]
    public void AriConfigTuple_ShouldDeserializeFromJson()
    {
        const string json = """{"attribute":"endpoint","value":"1000"}""";

        var tuple = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriConfigTuple);

        tuple.Should().NotBeNull();
        tuple!.Attribute.Should().Be("endpoint");
        tuple.Value.Should().Be("1000");
    }
}
