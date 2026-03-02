using Asterisk.Sdk.Hosting;
using DashboardExample.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAsteriskMultiServer();
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddSingleton<AsteriskMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AsteriskMonitorService>());

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
