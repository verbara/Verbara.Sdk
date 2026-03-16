using Asterisk.Sdk.Hosting;
using DashboardExample.Services;
using DashboardExample.Services.Repositories;
using DashboardExample.Services.Dialplan;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

app.MapRazorComponents<DashboardExample.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
