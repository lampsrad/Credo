using Credo.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Credo;

/// <summary>
/// A unit-of-work scope: owns a single DbContext for the lifetime of the scope.
/// Use via <c>await using var scope = repo.BeginScope();</c>. All Add/Get/SaveChanges
/// operations within a scope share the same context. Disposing the scope disposes the context.
/// </summary>
public sealed class RepoScope : IAsyncDisposable
{
    private readonly CredoDbContext cx;
    internal RepoScope(CredoDbContext context)
    {
        cx = context;
    }

    public void Add(object entity) => cx.Add(entity);
    public void AddRange<T>(IList<T> range) where T : class => cx.AddRange(range);
    public void RemoveRange<T>(IList<T> range) where T : class => cx.RemoveRange(range);
    public async Task<T?> GetEntityAsync<T>(Expression<Func<T, bool>>? filter = null) where T : class
    {
        IQueryable<T> query = RepoQuery.BuildQuery<T>(cx.Set<T>());
        return filter is null
            ? await query.SingleOrDefaultAsync()
            : await query.SingleOrDefaultAsync(filter);
    }
    public async Task<IList<T>> GetEntitiesAsync<T>(Expression<Func<T, bool>>? filter = null, Expression<Func<T, object>>? sort = null) where T : class
    {
        IQueryable<T> query = RepoQuery.BuildQuery<T>(cx.Set<T>());
        if (filter is not null) query = query.Where(filter);
        if (sort is not null) query = query.OrderBy(sort);
        return await query.ToListAsync();
    }
    public Task<int> SaveChangesAsync() => cx.SaveChangesAsync();
    public async ValueTask DisposeAsync() => await cx.DisposeAsync();
}

internal static class RepoQuery
{
    public static IQueryable<T> BuildQuery<T>(IQueryable<T> query) where T : class
    {
        if (typeof(T) == typeof(Portfolio))
        {
            return (query as IQueryable<Portfolio>)!
                .Include(x => x.security)
                .ThenInclude(x => x!.Transactions)
                .Include(x => x.security)
                .ThenInclude(x => x!.ticker)
                .Include(x => x.currency)
                .OrderBy(x => x.security!.ticker!.Name)
                .Cast<T>();
        }
        if (typeof(T) == typeof(Transaction))
        {
            return (query as IQueryable<Transaction>)!
                .OrderBy(x => x.TradeDate)
                .Cast<T>();
        }
        if (typeof(T) == typeof(Security))
        {
            return (query as IQueryable<Security>)!
                .Include(x => x.ticker)
                .Include(x => x.currency)
                .Include(x => x.Transactions)
                .OrderBy(x => x.ticker!.Name)
                .Cast<T>();
        }
        if (typeof(T) == typeof(Ticker))
        {
            return (query as IQueryable<Ticker>)!
                .OrderBy(x => x.Name)
                .Cast<T>();
        }
        if (typeof(T) == typeof(Currency))
        {
            return (query as IQueryable<Currency>)!
                .OrderBy(x => x.Name)
                .Cast<T>();
        }
        if (typeof(T) == typeof(History))
        {
            return (query as IQueryable<History>)!
                .OrderBy(x => x.Date)
                .Cast<T>();
        }
        if (typeof(T) == typeof(Watchlist))
        {
            return (query as IQueryable<Watchlist>)!
                .OrderBy(x => x.Symbol)
                .Cast<T>();
        }
        return query;
    }
}
