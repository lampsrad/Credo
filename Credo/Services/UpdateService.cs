using Credo.Models;
using YahooQuotesApi;

namespace Credo.Services;

public class UpdateService
{
    private readonly Repo repo;
    private readonly State state;

    public UpdateService(Repo repo, State state)
    {
        this.repo = repo;
        this.state = state;
    }

    public async Task UpdateSecuritiesAsync()
    {
        state.ShowProgress("Updating Securities");
        //try
        {
            await Task.Yield();
            var allTickers = await repo.GetEntitiesNTAsync<Ticker>(x => x.Symbol != "USDUSD=X");
            var watchlist  = await repo.GetEntitiesNTAsync<Models.Watchlist>(null);
            // Single snapshot covers both portfolio securities and watchlist symbols
            var allSnapshotSymbols = allTickers
                .Select(x => x.Symbol).Where(s => s is not null).Cast<string>()
                .Concat(watchlist.Select(w => w.Symbol).Where(s => s is not null).Cast<string>())
                .Distinct();
            state.UpdateProgress(10);
            var yahoo = new YahooQuotesBuilder().Build();
            var snapshotTask = yahoo.GetSnapshotAsync(allSnapshotSymbols);
            using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
            int progressPercent = 10;
            while (!snapshotTask.IsCompleted)
            {
                try { await tick.WaitForNextTickAsync(); }
                catch (OperationCanceledException) { break; }
                if (snapshotTask.IsCompleted) break;
                progressPercent = Math.Min(95, progressPercent + 10);
                state.UpdateProgress(progressPercent);
            }
            var snapshots = await snapshotTask;
            state.UpdateProgress(90);
            await using (var scope = repo.BeginScope())
            {
                var securities = await scope.GetEntitiesAsync<Security>(x => x.ticker != null);
                var currencies = await scope.GetEntitiesAsync<Currency>(x => x.Name != "USDUSD=X");
                foreach (var security in securities)
                {
                    var symbol = security.ticker?.Symbol;
                    if (symbol is null || !snapshots.TryGetValue(symbol, out var snapshot) || snapshot == null)
                    {
                        security.Price = 1;
                        continue;
                    }
                    security.Currency = snapshot.Currency.Name;
                    security.Price = snapshot.RegularMarketPrice;
                    security.PrevClose = snapshot.RegularMarketPreviousClose;
                    security.Exchange = snapshot.FullExchangeName;
                    security.ChangePercent = (decimal)snapshot.RegularMarketChangePercent;

                    security.DividendYield = (decimal)snapshot.DividendYield;
                    security.EPS = snapshot.EpsTrailingTwelveMonths;
                    security.PE = (decimal)snapshot.TrailingPE;
                    security.PEf = (decimal)snapshot.ForwardPE;
                    security.MarketCap = (decimal)snapshot.MarketCap;
                }
                foreach (var currency in currencies)
                {
                    if (currency.Symbol is null) continue;
                    if (!snapshots.TryGetValue(currency.Symbol, out var snapshot) || snapshot == null)
                        continue;
                    currency.Rate = snapshot.RegularMarketPrice;
                }
                await scope.SaveChangesAsync();
            }
            // Extract today's prices into a plain dictionary before passing on —
            // avoids referencing the internal YahooQuotesApi snapshot type by name.
            var todayPrices = snapshots
                .Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => (decimal?)kv.Value!.RegularMarketPrice);
            await UpdatePortfolioAsync();
            await AppendPortfolioHistoryAsync();
            await SyncAllHistoryAsync(todayPrices, allTickers, watchlist);
            state.UpdateProgress(100);
            state.Hide();
        }
        //catch (Exception ex)
        //{
        //    state.Hide();
        //    await state.ShowMessage("Update Failed", $"Update failed: {ex.Message}", "Ok");
        //}
    }
    public async Task UpdatePortfolioAsync()
    {
        await using var scope = repo.BeginScope();
        var ports = await scope.GetEntitiesAsync<Portfolio>();
        var total = ports.Sum(x => x.Market_Value);
        foreach (var p in ports)
        {
            p.Currency = p.security?.Currency;
            if (string.IsNullOrEmpty(p.Currency))
                p.Currency = Cash(p.Security_Description);
            p.Price = p.security?.Price;//local currency
            p.Market_Value = p.Currency == "USD=X" ? p.Price * p.Quantity : p.Price * p.Quantity * p.security?.currency?.Rate;//Usd converted
            p.Gain = (p.Market_Value - p.Cost);//Usd
            p.GainPerc = p.Price == 1 ? null : p.Gain / p.Cost * 100;
            p.Pct = p.Market_Value / total * 100;
            var trs = p.security?.Transactions.ToList();
            if (trs != null)
            {
                var bought = trs.Where(t => t.TranCode == "by" || t.TranCode == "li").Sum(t => t.Quantity ?? 0);
                var sold = trs.Where(t => t.TranCode == "sl" || t.TranCode == "lo").Sum(t => t.Quantity ?? 0);
                var saldo = bought - sold;
                if (saldo > 0)
                {
                    trs.Add(new Transaction
                    {
                        TranCode = "cv",
                        TradeDate = DateOnly.FromDateTime(DateTime.Now),
                        LocalAmount = saldo * (p.security?.Price ?? 0)
                    });
                }
                var irr = XirrCalculator.CalculateXirr(trs);
                if (!double.IsNaN(irr))
                    p.IRR = (decimal)irr * 100;
            }
        }
        await scope.SaveChangesAsync();
    }
    private async Task AppendPortfolioHistoryAsync()
    {
        await using var scope = repo.BeginScope();
        var ports = await scope.GetEntitiesAsync<Portfolio>();
        var total = Math.Round(ports.Sum(p => p.Market_Value ?? 0m), 2);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var row = await scope.GetEntityAsync<Models.History>(
            h => h.Symbol == "Portfolio" && h.Date == today);
        if (row is null)
            scope.Add(new Models.History { Symbol = "Portfolio", Date = today, Price = total });
        else
            row.Price = total;
        await scope.SaveChangesAsync();
    }
    /// <summary>
    /// Called at the end of UpdateSecuritiesAsync. For every symbol (securities + watchlist):
    ///   1. Back-fills any missing days since the last stored History row via GetHistoryAsync.
    ///   2. Upserts today using the snapshots already fetched — no extra Yahoo API call.
    /// This means missing days caused by not running the app are healed automatically.
    /// </summary>
    private async Task SyncAllHistoryAsync(
        Dictionary<string, decimal?> todayPrices,
        IList<Ticker> tickers,
        IList<Models.Watchlist> watchlist)
    {
        var allSymbols = tickers
            .Where(t => t.Symbol is not null).Select(t => t.Symbol!)
            .Concat(watchlist.Where(w => w.Symbol is not null).Select(w => w.Symbol!))
            .Distinct().ToList();

        if (allSymbols.Count == 0) return;

        await using var scope = repo.BeginScope();
        var existingHistory = await scope.GetEntitiesAsync<Models.History>(
            h => h.Symbol != null && allSymbols.Contains(h.Symbol));

        var existingKeys = existingHistory
            .Where(h => h.Symbol is not null)
            .Select(h => (h.Symbol!, h.Date))
            .ToHashSet();

        var lastDateBySymbol = existingHistory
            .Where(h => h.Symbol is not null)
            .GroupBy(h => h.Symbol!)
            .ToDictionary(g => g.Key, g => g.Max(h => h.Date));

        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        var defaultStart = DateOnly.FromDateTime(DateTime.Today.AddYears(-5));
        var toAdd = new List<Models.History>();

        // Back-fill any gap older than yesterday (Yahoo daily history lags ~1 day)
        foreach (var symbol in allSymbols)
        {
            var lastStored = lastDateBySymbol.TryGetValue(symbol, out var d) ? d : defaultStart;
            if (lastStored >= yesterday) continue; // already up to date

            var yahoo = new YahooQuotesBuilder()
                .WithHistoryStartDate(NodaTime.Instant.FromUtc(
                    lastStored.Year, lastStored.Month, lastStored.Day, 0, 0))
                .Build();
            await FetchTickerHistoryAsync(new Ticker { Symbol = symbol }, yahoo, existingKeys, toAdd);
        }

        // Upsert today using prices already in memory — no extra API call
        foreach (var symbol in allSymbols)
        {
            if (!todayPrices.TryGetValue(symbol, out var price) || price is null) continue;
            if (!existingKeys.Add((symbol, today))) continue;
            toAdd.Add(new Models.History { Symbol = symbol, Date = today, Price = price });
        }

        if (toAdd.Count > 0)
        {
            scope.AddRange(toAdd);
            await scope.SaveChangesAsync();
        }
    }

    public async Task UpdateHistoryAsync()
    {
        state.ShowProgress("Updating History");
        await using var scope = repo.BeginScope();
        var tickers = await scope.GetEntitiesAsync<Ticker>();
        if (tickers.Count == 0) { state.Hide(); return; }

        var existingHistory = await scope.GetEntitiesAsync<Models.History>();
        var existingKeys = existingHistory
            .Where(h => h.Symbol is not null)
            .Select(h => (h.Symbol!, h.Date))
            .ToHashSet();

        var lastDateBySymbol = existingHistory
            .Where(h => h.Symbol is not null)
            .GroupBy(h => h.Symbol!)
            .ToDictionary(g => g.Key, g => g.Max(h => h.Date));
        var defaultStart = DateOnly.FromDateTime(DateTime.Today.AddYears(-5));

        var toAdd = new List<Models.History>();
        int total = tickers.Count;
        int done = 0;
        foreach (var ticker in tickers)
        {
            var start = ticker.Symbol is not null && lastDateBySymbol.TryGetValue(ticker.Symbol, out var d)
                ? d
                : defaultStart;
            var yahoo = new YahooQuotesBuilder()
                .WithHistoryStartDate(NodaTime.Instant.FromUtc(start.Year, start.Month, start.Day, 0, 0))
                .Build();
            await FetchTickerHistoryAsync(ticker, yahoo, existingKeys, toAdd);
            done++;
            state.UpdateProgress(done * 100.0 / total, $"Updating History ({done}/{total})");
        }

        // Daily history from Yahoo doesn't include "today" until after market close
        // (and for indices like ^GSPC, often not even then). Use a live snapshot to
        // ensure every ticker has a row for today — same treatment securities get
        // via AppendSecuritiesHistoryAsync.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var symbols = tickers.Select(t => t.Symbol)
                             .Where(s => s is not null)
                             .Cast<string>()
                             .ToList();
        if (symbols.Count > 0)
        {
            var snapshotYahoo = new YahooQuotesBuilder().Build();
            var snapshots = await snapshotYahoo.GetSnapshotAsync(symbols);
            foreach (var ticker in tickers)
            {
                if (ticker.Symbol is null) continue;
                if (!existingKeys.Add((ticker.Symbol, today))) continue;
                if (!snapshots.TryGetValue(ticker.Symbol, out var snap) || snap == null) continue;
                toAdd.Add(new Models.History
                {
                    Symbol = ticker.Symbol,
                    Date = today,
                    Price = snap.RegularMarketPrice
                });
            }
        }

        if (toAdd.Count > 0)
        {
            scope.AddRange(toAdd);
            await scope.SaveChangesAsync();
        }
        state.UpdateProgress(100);
        state.Hide();
    }
    public async Task UpdateWatchlistHistoryAsync()
    {
        state.ShowProgress("Updating Watchlist History");
        await using var scope = repo.BeginScope();
        var watchlist = await scope.GetEntitiesAsync<Models.Watchlist>();
        if (watchlist.Count == 0) { state.Hide(); return; }

        var existingHistory = await scope.GetEntitiesAsync<Models.History>();
        var existingKeys = existingHistory
            .Where(h => h.Symbol is not null)
            .Select(h => (h.Symbol!, h.Date))
            .ToHashSet();

        var lastDateBySymbol = existingHistory
            .Where(h => h.Symbol is not null)
            .GroupBy(h => h.Symbol!)
            .ToDictionary(g => g.Key, g => g.Max(h => h.Date));
        var defaultStart = DateOnly.FromDateTime(DateTime.Today.AddYears(-5));

        var toAdd = new List<Models.History>();
        int total = watchlist.Count;
        int done = 0;

        foreach (var item in watchlist)
        {
            if (item.Symbol is null) continue;
            var start = lastDateBySymbol.TryGetValue(item.Symbol, out var d) ? d : defaultStart;
            var yahoo = new YahooQuotesBuilder()
                .WithHistoryStartDate(NodaTime.Instant.FromUtc(start.Year, start.Month, start.Day, 0, 0))
                .Build();
            var tempTicker = new Ticker { Symbol = item.Symbol };
            await FetchTickerHistoryAsync(tempTicker, yahoo, existingKeys, toAdd);
            done++;
            state.UpdateProgress(done * 100.0 / total, $"Watchlist History ({done}/{total})");
        }

        // Top-up with today's snapshot (Yahoo daily history lags until market close)
        var today = DateOnly.FromDateTime(DateTime.Today);
        var symbols = watchlist
            .Where(w => w.Symbol is not null)
            .Select(w => w.Symbol!)
            .ToList();
        if (symbols.Count > 0)
        {
            var snapshotYahoo = new YahooQuotesBuilder().Build();
            var snapshots = await snapshotYahoo.GetSnapshotAsync(symbols);
            foreach (var item in watchlist)
            {
                if (item.Symbol is null) continue;
                if (!existingKeys.Add((item.Symbol, today))) continue;
                if (!snapshots.TryGetValue(item.Symbol, out var snap) || snap == null) continue;
                toAdd.Add(new Models.History { Symbol = item.Symbol, Date = today, Price = snap.RegularMarketPrice });
            }
        }

        if (toAdd.Count > 0)
        {
            scope.AddRange(toAdd);
            await scope.SaveChangesAsync();
        }
        state.UpdateProgress(100);
        state.Hide();
    }

    private static async Task FetchTickerHistoryAsync(Ticker ticker, YahooQuotes yahoo,
     HashSet<(string, DateOnly)> existingKeys, List<Models.History> toAdd)
    {
        try
        {
            var result = await yahoo.GetHistoryAsync(ticker.Symbol!);
            if (!result.HasValue) return;

            var ticks = result.Value.Ticks
                .OrderBy(t => t.Date)                    // Ensure chronological order
                .Select(t => new
                {
                    Date = DateOnly.FromDateTime(t.Date.ToDateTimeUtc()),
                    Close = t.Close
                })
                .ToList();

            foreach (var current in ticks)
            {
                if (!existingKeys.Add((ticker.Symbol!, current.Date)))
                    continue;
                toAdd.Add(new Models.History
                {
                    Symbol = ticker.Symbol,
                    Date = current.Date,
                    Price = (decimal?)current.Close
                });
            }
        }
        catch (ArgumentException) { /* skip tickers with symbols Yahoo Finance rejects */ }
    }

    private static string? Cash(string? secname)
    {
        if (string.IsNullOrWhiteSpace(secname))
            return null;
        string upperName = secname.ToUpperInvariant();
        return upperName switch
        {
            var s when s.StartsWith("AUSTRALIAN DOLLAR") => "AUD=X",
            var s when s.StartsWith("POUND STERLING") => "GBP=X",
            var s when s.StartsWith("EURO") => "EUR=X",
            var s when s.StartsWith("US DOLLAR") => "USD=X",
            _ => null
        };
    }
}
