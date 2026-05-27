using Credo;
using Credo.Components;
using Credo.Services;
using Microsoft.EntityFrameworkCore;
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
app.UseStaticFiles();
app.MapStaticAssets();
app.MapGet("/help.html", async (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "StaticPages", "help.html");
    var html = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Lifetime.ApplicationStopping.Register(() => appConfig.StopBrowser());

// Create Watchlist table if it does not already exist (no migrations in this project).
using (var startupScope = app.Services.CreateScope())
{
    var factory = startupScope.ServiceProvider.GetRequiredService<IDbContextFactory>();
    await using var ctx = factory.CreateDbContext();
    await ctx.Database.ExecuteSqlRawAsync(@"
        IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='WatchlistItems' AND xtype='U')
        CREATE TABLE WatchlistItems (
            Id   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            Symbol NVARCHAR(50)  NULL,
            Name   NVARCHAR(300) NULL
        )");
}

app.Run();
