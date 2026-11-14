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
    // Kestrel is not listening until app.Run(). Opening Chrome earlier shows "This site can't be reached".
    app.Lifetime.ApplicationStarted.Register(() => appConfig.StartBrowser(url));
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseStaticFiles();
app.MapStaticAssets();
app.MapGet("/help.html", async (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "StaticPages", "help.html");
    var html = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapGet("/developer.html", async (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "StaticPages", "developer.html");
    var html = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Lifetime.ApplicationStopping.Register(() => appConfig.StopBrowser());

app.Run();
