using CoopGameServer.Admin.Components;
using CoopGameServer.Admin.Services;

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
