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
            var tickerSymbols = allTickers.Select(x => x.Symbol).Where(s => s is not null).Cast<string>();
            state.UpdateProgress(10);
            var yahoo = new YahooQuotesBuilder().Build();
            var snapshotTask = yahoo.GetSnapshotAsync(tickerSymbols);
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
                    security.FiftyDayAverage = (decimal)snapshot.FiftyDayAverage;
                    security.TwoHundredDayAverage = (decimal)snapshot.TwoHundredDayAverage;
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
            await UpdatePortfolioAsync();
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

        var lastDate = existingHistory.Count > 0
            ? existingHistory.Max(h => h.Date)
            : DateOnly.Parse("2015-01-01");

        var yahoo = new YahooQuotesBuilder()
            .WithHistoryStartDate(NodaTime.Instant.FromUtc(lastDate.Year, lastDate.Month, lastDate.Day, 0, 0))
            .Build();

        var toAdd = new List<Models.History>();
        int total = tickers.Count;
        int done = 0;
        foreach (var ticker in tickers)
        {
            await FetchTickerHistoryAsync(ticker, yahoo, existingKeys, toAdd);
            done++;
            state.UpdateProgress(done * 100.0 / total, $"Updating History ({done}/{total})");
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
        if (ticker.Symbol!.EndsWith("=X")) return;
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

            for (int i = 0; i < ticks.Count; i++)
            {
                var current = ticks[i];
                if (!existingKeys.Add((ticker.Symbol!, current.Date)))
                    continue;

                decimal? ma50 = null;

                // Calculate 50-day MA only when we have at least 50 days
                if (i >= 49)
                {
                    var sum = 0.0;
                    for (int j = 0; j < 50; j++)
                    {
                        sum += ticks[i - j].Close;
                    }
                    ma50 = (decimal)(sum / 50);
                }

                toAdd.Add(new Models.History
                {
                    Symbol = ticker.Symbol,
                    Date = current.Date,
                    Price = double.IsNaN(current.Close) ? null : (decimal)current.Close,
                    FiftyDayMA = ma50
                });
            }
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
