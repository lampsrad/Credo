using Credo.Models;
using Microsoft.EntityFrameworkCore;

namespace Credo;

public interface IDbContextFactory
{
    CredoDbContext CreateDbContext();
}
public class DbContextFactory : IDbContextFactory
{
    private readonly AppConfig appConfig;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;
    public DbContextFactory(AppConfig _appConfig, IConfiguration _config, IHostEnvironment _env)
    {
        appConfig = _appConfig;
        configuration = _config;
        environment = _env;
    }
    public CredoDbContext CreateDbContext()
    {
        var con = Resolve(appConfig.ConnectionKey)
                  ?? Resolve(appConfig.MachineName)
                  ?? Resolve("Local");
        if (string.IsNullOrWhiteSpace(con))
            throw new InvalidOperationException(
                $"No connection string found for '{appConfig.ConnectionKey}', '{appConfig.MachineName}', or 'Local'. " +
                $"Set it via User Secrets: dotnet user-secrets set \"ConnectionStrings:{appConfig.ConnectionKey}\" \"<connection-string>\" --project Credo");
        var opsbuilder = new DbContextOptionsBuilder<CredoDbContext>();
        opsbuilder.UseSqlServer(con);
        if (environment.IsDevelopment())
            opsbuilder.EnableSensitiveDataLogging();
        var context = new CredoDbContext(opsbuilder.Options);
        return context;
    }

    private string? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var raw = configuration.GetConnectionString(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw
            .Replace("[ServerName]", $"{appConfig.MachineName}\\{gData.ServerName}")
            .Replace("[DatabaseName]", appConfig.DbName);
    }
}
