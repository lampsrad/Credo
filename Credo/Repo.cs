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
    public async Task<IList<int>> GetTransactionYearsAsync()
    {
        await using var cx = dbFactory.CreateDbContext();
        return await cx.Transactions
            .Select(t => t.TradeDate.Year)
            .Distinct()
            .OrderBy(y => y)
            .ToListAsync();
    }
    public async Task<T?> GetEntityNTAsync<T>(Expression<Func<T, bool>>? filter = null) where T : class
    {
        await using var cx = dbFactory.CreateDbContext();
        await WatchlistSchema.EnsureIfWatchlistAsync<T>(cx);
        IQueryable<T> query = RepoQuery.BuildQuery<T>(cx.Set<T>().AsNoTracking());
        return filter is null
            ? await query.SingleOrDefaultAsync()
            : await query.SingleOrDefaultAsync(filter);
    }
    public async Task<IList<T>> GetEntitiesNTAsync<T>(Expression<Func<T, bool>>? filter = null, Expression<Func<T, object>>? sort = null) where T : class
    {
        await using var cx = dbFactory.CreateDbContext();
        await WatchlistSchema.EnsureIfWatchlistAsync<T>(cx);
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
        CopyBackupToOneDrive(fn, appConfig.BackupOneDrivePath);
    }
    public async Task SqlRestoreAsync(string fn)
    {
        fn = CopyFromOneDrive(fn, appConfig.BackupOneDrivePath);
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
    static void CopyBackupToOneDrive(string localFile, string? oneDriveFolder)
    {
        if (string.IsNullOrWhiteSpace(oneDriveFolder))
            throw new InvalidOperationException("Paths:BackupOneDrive is not set in appsettings.json.");
        string destination = Path.Combine(oneDriveFolder, Path.GetFileName(localFile));
        try
        {
            Directory.CreateDirectory(oneDriveFolder);
            File.Copy(localFile, destination, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"SQL backup succeeded at {localFile}.{Environment.NewLine}Copy to OneDrive failed for {destination}.{Environment.NewLine}{ex.Message}", ex);
        }
    }
    static string CopyFromOneDrive(string localFile, string? oneDriveFolder)
    {
        if (string.IsNullOrWhiteSpace(oneDriveFolder))
            throw new InvalidOperationException("Paths:BackupOneDrive is not set in appsettings.json.");
        string name = Path.GetFileName(localFile);
        string source = Path.Combine(oneDriveFolder, name);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Backup file '{name}' was not found in {oneDriveFolder}.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            File.Copy(source, localFile, true);
            return localFile;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not copy {source} to {localFile}. Restore was not started.{Environment.NewLine}{ex.Message}", ex);
        }
    }
}

/// <summary>
/// Creates WatchlistItems on the first watchlist query. There is no EF migration, and startup does not open the database.
/// </summary>
internal static class WatchlistSchema
{
    private static int ready;

    internal static Task EnsureIfWatchlistAsync<T>(CredoDbContext cx) where T : class =>
        typeof(T) == typeof(Watchlist) ? EnsureAsync(cx) : Task.CompletedTask;

    private static async Task EnsureAsync(CredoDbContext cx)
    {
        if (System.Threading.Volatile.Read(ref ready) == 1)
            return;

        await cx.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='WatchlistItems' AND xtype='U')
            CREATE TABLE WatchlistItems (
                Id   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Symbol NVARCHAR(50)  NULL,
                Name   NVARCHAR(300) NULL
            )");

        System.Threading.Volatile.Write(ref ready, 1);
    }
}
