using Asterisk.Sdk.Hosting;
using PbxAdmin.Services;
using PbxAdmin.Services.Repositories;
using PbxAdmin.Services.Dialplan;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.File(
        new Serilog.Formatting.Json.JsonFormatter(),
        path: "logs/dashboard-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAsteriskMultiServer();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddSingleton<CallFlowTracker>();
builder.Services.AddSingleton<AsteriskMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AsteriskMonitorService>());
builder.Services.AddSingleton<PbxConfigManager>();

builder.Services.AddSingleton<ConfigOperationState>();
builder.Services.AddSingleton<IConfigProviderResolver, ConfigProviderResolver>();
builder.Services.AddSingleton<TrunkService>();
builder.Services.AddSingleton<ExtensionService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<IRouteRepositoryResolver, RouteRepositoryResolver>();
builder.Services.AddSingleton<IDialplanProviderResolver, DialplanProviderResolver>();
builder.Services.AddSingleton<DialplanRegenerator>();
builder.Services.AddSingleton<RouteService>();
builder.Services.AddSingleton<TimeConditionService>();

builder.Services.AddSingleton<IQueueConfigRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbQueueConfigRepository");
    var logger = sp.GetRequiredService<ILogger<DbQueueConfigRepository>>();
    return new DbQueueConfigRepository(connStr, logger);
});
builder.Services.AddSingleton<IQueueViewManager, QueueViewManager>();
builder.Services.AddSingleton<QueueConfigService>();

builder.Services.AddSingleton<IIvrMenuRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbIvrMenuRepository");
    var logger = sp.GetRequiredService<ILogger<DbIvrMenuRepository>>();
    return new DbIvrMenuRepository(connStr, logger);
});
builder.Services.AddSingleton<IvrMenuService>();

// Recording + MOH services
builder.Services.AddSingleton<IRecordingMohSchemaManager, RecordingMohSchemaManager>();
builder.Services.AddSingleton<AudioFileService>();

builder.Services.AddSingleton<IRecordingPolicyRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbRecordingPolicyRepository");
    var logger = sp.GetRequiredService<ILogger<DbRecordingPolicyRepository>>();
    return new DbRecordingPolicyRepository(connStr, logger);
});
builder.Services.AddSingleton<RecordingService>();

builder.Services.AddSingleton<IMohClassRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbMohClassRepository");
    var logger = sp.GetRequiredService<ILogger<DbMohClassRepository>>();
    return new DbMohClassRepository(connStr, logger);
});
builder.Services.AddSingleton<MohService>();

builder.Services.AddSingleton<IConferenceConfigRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbConferenceConfigRepository");
    var logger = sp.GetRequiredService<ILogger<DbConferenceConfigRepository>>();
    return new DbConferenceConfigRepository(connStr, logger);
});
builder.Services.AddSingleton<ConferenceConfigService>();

builder.Services.AddSingleton<IFeatureCodeRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetSection("Asterisk:Servers").GetChildren()
        .Where(s => string.Equals(s["ConfigMode"], "Realtime", StringComparison.OrdinalIgnoreCase))
        .Select(s => s["RealtimeConnectionString"])
        .FirstOrDefault()
        ?? cfg.GetConnectionString("QueueConfig")
        ?? throw new InvalidOperationException("No Realtime connection string for DbFeatureCodeRepository");
    var logger = sp.GetRequiredService<ILogger<DbFeatureCodeRepository>>();
    return new DbFeatureCodeRepository(connStr, logger);
});
builder.Services.AddSingleton<FeatureCodeService>();

builder.Services.AddScoped<SelectedServerService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous();

app.MapRazorComponents<PbxAdmin.Components.App>()
    .AddInteractiveServerRenderMode();

// --- Audio file API endpoints ---
var api = app.MapGroup("/api").RequireAuthorization();

// Recordings: read-only file access
var recApi = api.MapGroup("/recordings");

recApi.MapGet("/files", async (
    string? path, string? filter,
    RecordingService svc, IConfiguration config,
    HttpContext ctx) =>
{
    var serverId = ctx.Request.Query["serverId"].FirstOrDefault();
    var storagePath = path
        ?? (serverId is not null ? config[$"Asterisk:Servers:{serverId}:RecordingsPath"] : null)
        ?? "/var/spool/asterisk/monitor";
    var files = await svc.GetRecordingFilesAsync(storagePath, filter);
    return Results.Ok(files);
});

recApi.MapGet("/files/{filename}", (
    string filename, string? path, bool? download,
    RecordingService svc, IConfiguration config,
    HttpContext ctx) =>
{
    var serverId = ctx.Request.Query["serverId"].FirstOrDefault();
    var storagePath = path
        ?? (serverId is not null ? config[$"Asterisk:Servers:{serverId}:RecordingsPath"] : null)
        ?? "/var/spool/asterisk/monitor";
    var stream = svc.GetRecordingStream(storagePath, filename);
    if (stream is null) return Results.NotFound();

    var contentType = AudioFileService.GetContentType(filename);
    if (download == true)
        return Results.Stream(stream, contentType, filename);
    return Results.Stream(stream, contentType);
});

// MOH: file management (read + write)
var mohApi = api.MapGroup("/moh");

mohApi.MapGet("/{classId:int}/files", async (int classId, MohService svc) =>
{
    var files = await svc.GetAudioFilesAsync(classId);
    return Results.Ok(files);
});

mohApi.MapGet("/{classId:int}/files/{filename}", async (int classId, string filename, MohService svc) =>
{
    var stream = await svc.GetAudioStreamAsync(classId, filename);
    if (stream is null) return Results.NotFound();
    return Results.Stream(stream, AudioFileService.GetContentType(filename));
});

mohApi.MapPost("/{classId:int}/files", async (
    int classId, IFormFile file, MohService svc,
    IConfiguration config, HttpContext ctx) =>
{
    var serverId = ctx.Request.Query["serverId"].FirstOrDefault() ?? "default";
    var maxFile = config.GetValue($"Asterisk:Servers:{serverId}:MaxUploadSizeMb", 20);
    var maxClass = config.GetValue($"Asterisk:Servers:{serverId}:MaxMohClassSizeMb", 200);

    using var stream = file.OpenReadStream();
    var (success, error) = await svc.UploadAudioAsync(
        classId, file.FileName, stream, file.Length, maxFile, maxClass);

    return success ? Results.Ok() : Results.BadRequest(error);
}).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(20 * 1024 * 1024));

mohApi.MapDelete("/{classId:int}/files/{filename}", async (
    int classId, string filename, MohService svc) =>
{
    var (success, error) = await svc.DeleteAudioAsync(classId, filename);
    return success ? Results.Ok() : Results.BadRequest(error);
});

app.Run();
