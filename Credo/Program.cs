using Credo;
using Credo.Components;
using Credo.Services;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<AppConfig>();
builder.Services.AddSingleton<IDbContextFactory, DbContextFactory>();
builder.Services.AddScoped<Repo>();
builder.Services.AddScoped<State>();
builder.Services.AddScoped<UpdateService>();
builder.Services.AddScoped<GraphService>();
var app = builder.Build();
var appConfig = app.Services.GetRequiredService<AppConfig>();
// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    string url = app.Configuration["Kestrel:Endpoints:Http:Url"] ?? string.Empty;
    appConfig.StartBrowser(url);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Lifetime.ApplicationStopping.Register(() => appConfig.StopBrowser());

app.Run();
