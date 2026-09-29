using CoopGameServer.Admin.Components;
using CoopGameServer.Admin.Health;
using CoopGameServer.Admin.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient("GameApi", client =>
{
    var baseUrl = builder.Configuration["AdminApi:BaseUrl"] ?? "https://localhost:7238";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped<AdminSession>();
builder.Services.AddScoped<AdminApiClient>();
builder.Services.AddScoped<AdminRewardSubmissionState>();
builder.Services.AddScoped<AdminOperationsState>();
builder.Services.AddHealthChecks()
    .AddCheck<ApiReadinessHealthCheck>(
        "api",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (builder.Configuration.GetValue("HttpsRedirection:Enabled", true))
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});
app.Run();
