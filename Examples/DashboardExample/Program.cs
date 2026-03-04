using Asterisk.Sdk.Hosting;
using DashboardExample.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAsteriskMultiServer();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddSingleton<CallFlowTracker>();
builder.Services.AddSingleton<AsteriskMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AsteriskMonitorService>());
builder.Services.AddSingleton<PbxConfigManager>();

var configProviderType = builder.Configuration["ConfigProvider:Type"] ?? "Ami";
if (string.Equals(configProviderType, "Database", StringComparison.OrdinalIgnoreCase))
{
    var connStr = builder.Configuration["ConfigProvider:ConnectionString"]
        ?? throw new InvalidOperationException("ConfigProvider:ConnectionString is required when Type is 'Database'.");
    builder.Services.AddSingleton<IConfigProvider>(sp =>
        new DbConfigProvider(connStr, sp.GetRequiredService<PbxConfigManager>(), sp.GetRequiredService<ILogger<DbConfigProvider>>()));
}
else
{
    builder.Services.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<PbxConfigManager>());
}

builder.Services.AddSingleton<TrunkService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<DashboardExample.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
