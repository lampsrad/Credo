using Credo.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Credo;

/// <summary>
/// Provides one-shot read helpers and a unit-of-work factory for write workflows.
/// Read methods (AnyEntityAsync, GetEntityNTAsync, GetEntitiesNTAsync) create + dispose
/// a fresh DbContext per call. Write workflows MUST use <see cref="BeginScope"/>.
/// </summary>
public class Repo
{
    private readonly IDbContextFactory dbFactory;
    private readonly AppConfig appConfig;

    public Repo(IDbContextFactory _dbFactory, AppConfig _appConfig)
    {
        dbFactory = _dbFactory;
        appConfig = _appConfig;
    }

    public RepoScope BeginScope() => new RepoScope(dbFactory.CreateDbContext());
    public async Task<bool> AnyEntityAsync<T>(Expression<Func<T, bool>>? filter = null) where T : class
    {
        await using var cx = dbFactory.CreateDbContext();
        IQueryable<T> query = cx.Set<T>();
        return filter is null ? await query.AnyAsync() : await query.AnyAsync(filter);
    }
    public async Task<DateOnly?> GetLatestHistoryDateAsync()
    {
        await using var cx = dbFactory.CreateDbContext();
        return await cx.Set<History>().MaxAsync(h => (DateOnly?)h.Date);
    }
    public async Task<DateOnly?> GetLatestTransactionDateAsync()
    {
        await using var cx = dbFactory.CreateDbContext();
        return await cx.Transactions.MaxAsync(t => (DateOnly?)t.TradeDate);
    }
    public async Task<T?> GetEntityNTAsync<T>(Expression<Func<T, bool>>? filter = null) where T : class
    {
        await using var cx = dbFactory.CreateDbContext();
        IQueryable<T> query = RepoQuery.BuildQuery<T>(cx.Set<T>().AsNoTracking());
        return filter is null
            ? await query.SingleOrDefaultAsync()
            : await query.SingleOrDefaultAsync(filter);
    }
    public async Task<IList<T>> GetEntitiesNTAsync<T>(Expression<Func<T, bool>>? filter = null, Expression<Func<T, object>>? sort = null) where T : class
    {
        await using var cx = dbFactory.CreateDbContext();
        IQueryable<T> query = RepoQuery.BuildQuery<T>(cx.Set<T>().AsNoTracking());
        if (filter is not null) query = query.Where(filter);
        if (sort is not null) query = query.OrderBy(sort);
        return await query.ToListAsync();
    }
    public async Task SqlBackupAsync(string fn)
    {
        await using var cx = dbFactory.CreateDbContext();
        await cx.Database.ExecuteSqlInterpolatedAsync(
            $"Use Credo Backup Database Credo to Disk = {fn} with init");
    }
    public async Task SqlRestoreAsync(string fn)
    {
        await using var cx = dbFactory.CreateDbContext();
        // DbName is a config-driven SQL identifier; validate to avoid injection via misconfiguration.
        if (!System.Text.RegularExpressions.Regex.IsMatch(appConfig.DbName, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException($"Invalid database name '{appConfig.DbName}'.");
        var dbName = appConfig.DbName;
#pragma warning disable EF1002 // dbName is validated above; fn is parameterized.
        var sql = $"USE master; ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{dbName}] FROM DISK = @filePath WITH REPLACE;";
        await cx.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@filePath", fn));
        await cx.Database.ExecuteSqlRawAsync($"USE master; ALTER DATABASE [{dbName}] SET MULTI_USER;");
#pragma warning restore EF1002
    }
}
