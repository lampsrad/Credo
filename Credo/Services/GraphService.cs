using Credo.Models;
using System.Globalization;
using YahooQuotesApi;
using History = Credo.Models.History;

namespace Credo.Services;

public record ChartData(
    string[] Labels,
    decimal[] Data,
    decimal?[]? SpyData,
    decimal?[]? TradeData,
    decimal?[]? SellTradeData,
    string? Title
);


public class GraphService(Repo repo)
{
    public async Task<ChartData?> LoadPortfolioDataAsync()
    {
        var mvRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == "Portfolio", h => h.Date);
        if (mvRows.Count == 0) return null;

        var labels = mvRows.Select(h => h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var data = mvRows.Select(h => h.Price ?? 0m).ToArray();

        var spyRows = await repo.GetEntitiesNTAsync<History>(x => x.Symbol == "^GSPC", s => s.Date);
        var spyData = AlignSpy(spyRows, mvRows.Select(h => h.Date));

        return new ChartData(labels, data, spyData, null, null, null);
    }

    /// <summary>
    /// Returns ChartData for any symbol. Fast path: reads from the local History table if rows
    /// exist (watchlist or regular security). Slow path: live Yahoo fetch for unknown symbols.
    /// </summary>
    public async Task<ChartData?> LoadAdhocTickerDataAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        // ── Fast path: symbol already in History table ───────────────────────────
        var historyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == symbol, h => h.Date);
        if (historyRows.Count > 0)
        {
            var labelsDb = historyRows
                .Select(h => h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToArray();
            var dataDb = historyRows.Select(h => h.Price ?? 0m).ToArray();
            var spyRowsDb = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == "^GSPC", h => h.Date);
            var spyDataDb = AlignSpy(spyRowsDb, historyRows.Select(h => h.Date));
            // Prefer a saved watchlist name; fall back to bare symbol
            var watchEntry = await repo.GetEntityNTAsync<Watchlist>(w => w.Symbol == symbol);
            var titleDb = watchEntry?.Name is not null ? $"{watchEntry.Name} ({symbol})" : symbol;
            return new ChartData(labelsDb, dataDb, spyDataDb, null, null, titleDb);
        }

        // ── Slow path: live Yahoo fetch (symbol not yet in DB) ───────────────────
        var startDate = DateTime.Today.AddYears(-5);
        var start = NodaTime.Instant.FromUtc(startDate.Year, startDate.Month, startDate.Day, 0, 0);
        var yahoo = new YahooQuotesBuilder().WithHistoryStartDate(start).Build();

        IList<(DateOnly Date, decimal Close)> ticks;
        try
        {
            var result = await yahoo.GetHistoryAsync(symbol);
            if (!result.HasValue) return null;
            ticks = result.Value.Ticks
                .OrderBy(t => t.Date)
                .Select(t => (Date: DateOnly.FromDateTime(t.Date.ToDateTimeUtc()), Close: (decimal)t.Close))
                .ToList();
        }
        catch (ArgumentException) { return null; }
        if (ticks.Count == 0) return null;

        var labels = ticks.Select(t => t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var data   = ticks.Select(t => t.Close).ToArray();

        var spyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == "^GSPC", h => h.Date);
        var spyData = AlignSpy(spyRows, ticks.Select(t => t.Date));

        string title = symbol;
        try
        {
            var snapshots = await new YahooQuotesBuilder().Build().GetSnapshotAsync(new[] { symbol });
            if (snapshots.TryGetValue(symbol, out var snap) && snap is not null)
            {
                var name = snap.LongName ?? snap.ShortName;
                if (!string.IsNullOrWhiteSpace(name))
                    title = $"{name} ({symbol})";
            }
        }
        catch { /* fall back to symbol */ }

        return new ChartData(labels, data, spyData, null, null, title);
    }

    public async Task<ChartData?> LoadTickerDataAsync(string symbol, int? secId)
    {
        var historyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == symbol, h => h.Date);
        if (historyRows.Count == 0) return null;
        var labels = historyRows.Select(h => h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var data = historyRows.Select(h => h.Price ?? 0m).ToArray();
        var spyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == "^GSPC", h => h.Date);
        var spyData = AlignSpy(spyRows, historyRows.Select(h => h.Date));
        var buyTrades = secId is null
            ? new List<Transaction>()
            : await repo.GetEntitiesNTAsync<Transaction>(
                t => t.SecurityID == secId && (t.TranCode == "BY" || t.TranCode == "LI"),
                t => t.TradeDate);
        var sellTrades = secId is null
            ? new List<Transaction>()
            : await repo.GetEntitiesNTAsync<Transaction>(
                t => t.SecurityID == secId && (t.TranCode == "SL" || t.TranCode == "LO"),
                t => t.TradeDate);
        var buyMarks = new decimal?[historyRows.Count];
        foreach (var t in buyTrades)
        {
            int idx = FindDateIndex(historyRows, t.TradeDate);
            buyMarks[idx] = historyRows[idx].Price ?? data[idx];
        }
        var sellMarks = new decimal?[historyRows.Count];
        foreach (var t in sellTrades)
        {
            int idx = FindDateIndex(historyRows, t.TradeDate);
            sellMarks[idx] = historyRows[idx].Price ?? data[idx];
        }
        return new ChartData(labels, data, spyData, buyMarks, sellMarks, null);
    }
    public async Task<ChartData?> ComputePortfolioChartAsync(HashSet<int>? excludedSecurityIds = null)
    {
        var securities = await repo.GetEntitiesNTAsync<Security>(null);
        if (securities.Count == 0) return null;

        var firstBuy = securities
            .SelectMany(s => s.Transactions)
            .Where(t => IsBuy(t.TranCode))
            .Select(t => (DateOnly?)t.TradeDate)
            .Min();
        if (firstBuy is null) return null;

        var included = excludedSecurityIds is { Count: > 0 }
            ? securities.Where(s => !excludedSecurityIds.Contains(s.Id)).ToList()
            : securities;

        var secSymbols = included
            .Where(s => s.ticker?.Symbol is not null)
            .Select(s => s.ticker!.Symbol!)
            .Distinct().ToList();
        var fxSymbols = included
            .Where(s => !string.IsNullOrEmpty(s.Currency) && s.Currency != "USD=X")
            .Select(s => s.Currency!)
            .Distinct().ToList();
        var allSymbols = secSymbols.Concat(fxSymbols).Distinct().ToList();

        var allHistory = await repo.GetEntitiesNTAsync<History>(
            h => h.Symbol != null && allSymbols.Contains(h.Symbol), h => h.Date);
        if (allHistory.Count == 0) return null;

        var bySymbol = allHistory
            .GroupBy(h => h.Symbol!)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Date).ToList());

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dates = allHistory
            .Where(h => h.Date >= firstBuy.Value && h.Date <= today)
            .Select(h => h.Date)
            .Distinct().OrderBy(d => d).ToList();

        var secTrans = included.ToDictionary(
            s => s.Id,
            s => s.Transactions.OrderBy(t => t.TradeDate).ToList());

        var labels = new List<string>(dates.Count);
        var data   = new List<decimal>(dates.Count);

        foreach (var date in dates)
        {
            decimal total = 0m;
            foreach (var sec in included)
            {
                var sym = sec.ticker?.Symbol;
                if (sym is null) continue;

                int qty = 0;
                foreach (var t in secTrans[sec.Id])
                {
                    if (t.TradeDate > date) break;
                    if (IsBuy(t.TranCode))  qty += t.Quantity ?? 0;
                    else if (IsSell(t.TranCode)) qty -= t.Quantity ?? 0;
                }
                if (qty <= 0) continue;

                if (!bySymbol.TryGetValue(sym, out var hist)) continue;
                var priceRow = LastOnOrBefore(hist, date);
                if (priceRow?.Price is null) continue;
                var price = priceRow.Price.Value;

                decimal fx = 1m;
                var cur = sec.Currency;
                if (!string.IsNullOrEmpty(cur) && cur != "USD=X")
                {
                    if (!bySymbol.TryGetValue(cur, out var fxHist)) continue;
                    var fxRow = LastOnOrBefore(fxHist, date);
                    if (fxRow?.Price is null) continue;
                    fx = fxRow.Price.Value;
                }

                total += qty * price * fx;
            }
            labels.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            data.Add(Math.Round(total, 2));
        }

        var spyRows = await repo.GetEntitiesNTAsync<History>(x => x.Symbol == "^GSPC", s => s.Date);
        var spyData = AlignSpy(spyRows, dates);

        return new ChartData(labels.ToArray(), data.ToArray(), spyData, null, null, null);
    }

    public ChartData SliceByRange(ChartData full, string range)
    {
        bool isDaily = full.Labels.Length > 0 && full.Labels[0].Length == 10;
        int take = range switch
        {
            "1y" => isDaily ? 252 : 12,
            "2y" => isDaily ? 504 : 24,
            "5y" => isDaily ? 1260 : 60,
            _ => full.Labels.Length
        };
        int skip = Math.Max(0, full.Labels.Length - take);
        return new ChartData(
            full.Labels.Skip(skip).ToArray(),
            full.Data.Skip(skip).ToArray(),
            full.SpyData?.Skip(skip).ToArray(),
            full.TradeData?.Skip(skip).ToArray(),
            full.SellTradeData?.Skip(skip).ToArray(),
            full.Title
        );
    }

    private static int FindDateIndex(IList<History> rows, DateOnly date)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Date >= date) return i;
        return rows.Count - 1;
    }

    // Future Use: one-off recompute of the full Symbol="Portfolio" series in History
    // from transactions + per-ticker price history + FX history. Idempotent (wipes and
    // reinserts). Not wired to any UI — invoke manually if the series ever needs rebuilding.
    public async Task<int> BackfillPortfolioHistoryAsync()
    {
        const string PortfolioSymbol = "Portfolio";

        var securities = await repo.GetEntitiesNTAsync<Security>(null);
        if (securities.Count == 0) return 0;

        var firstBuy = securities
            .SelectMany(s => s.Transactions)
            .Where(t => IsBuy(t.TranCode))
            .Select(t => (DateOnly?)t.TradeDate)
            .Min();
        if (firstBuy is null) return 0;

        var secSymbols = securities
            .Where(s => s.ticker?.Symbol is not null)
            .Select(s => s.ticker!.Symbol!)
            .Distinct()
            .ToList();
        var fxSymbols = securities
            .Where(s => !string.IsNullOrEmpty(s.Currency) && s.Currency != "USD=X")
            .Select(s => s.Currency!)
            .Distinct()
            .ToList();
        var allSymbols = secSymbols.Concat(fxSymbols).Distinct().ToList();

        var allHistory = await repo.GetEntitiesNTAsync<History>(
            h => h.Symbol != null && allSymbols.Contains(h.Symbol),
            h => h.Date);
        if (allHistory.Count == 0) return 0;

        var bySymbol = allHistory
            .GroupBy(h => h.Symbol!)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Date).ToList());

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dates = allHistory
            .Where(h => h.Date >= firstBuy.Value && h.Date <= today)
            .Select(h => h.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var secTrans = securities.ToDictionary(
            s => s.Id,
            s => s.Transactions.OrderBy(t => t.TradeDate).ToList());

        var results = new List<History>(dates.Count);
        foreach (var date in dates)
        {
            decimal total = 0m;
            foreach (var sec in securities)
            {
                var sym = sec.ticker?.Symbol;
                if (sym is null) continue;

                int qty = 0;
                foreach (var t in secTrans[sec.Id])
                {
                    if (t.TradeDate > date) break;
                    if (IsBuy(t.TranCode)) qty += t.Quantity ?? 0;
                    else if (IsSell(t.TranCode)) qty -= t.Quantity ?? 0;
                }
                if (qty <= 0) continue;

                if (!bySymbol.TryGetValue(sym, out var hist)) continue;
                var priceRow = LastOnOrBefore(hist, date);
                if (priceRow?.Price is null) continue;
                var price = priceRow.Price.Value;

                decimal fx = 1m;
                var cur = sec.Currency;
                if (!string.IsNullOrEmpty(cur) && cur != "USD=X")
                {
                    if (!bySymbol.TryGetValue(cur, out var fxHist)) continue;
                    var fxRow = LastOnOrBefore(fxHist, date);
                    if (fxRow?.Price is null) continue;
                    fx = fxRow.Price.Value;
                }

                total += qty * price * fx;
            }
            results.Add(new History { Symbol = PortfolioSymbol, Date = date, Price = Math.Round(total, 2) });
        }

        await using var scope = repo.BeginScope();
        var existing = await scope.GetEntitiesAsync<History>(h => h.Symbol == PortfolioSymbol);
        if (existing.Count > 0) scope.RemoveRange(existing);
        scope.AddRange(results);
        await scope.SaveChangesAsync();
        return results.Count;
    }

    private static bool IsBuy(string? code) =>
        string.Equals(code, "by", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "li", StringComparison.OrdinalIgnoreCase);
    private static bool IsSell(string? code) =>
        string.Equals(code, "sl", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "lo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Aligns SPY (^GSPC) history to a target date series, forward-filling missing dates
    /// (e.g. today, before Yahoo publishes the close) with the most recent known value.
    /// Leading dates before SPY history begins remain null.
    /// </summary>
    private static decimal?[]? AlignSpy(IList<History> spyRows, IEnumerable<DateOnly> targetDates)
    {
        if (spyRows.Count == 0) return null;
        var sorted = spyRows.OrderBy(s => s.Date).ToList();
        var result = new List<decimal?>();
        int i = 0;
        decimal? last = null;
        foreach (var d in targetDates)
        {
            while (i < sorted.Count && sorted[i].Date <= d)
            {
                if (sorted[i].Price is not null) last = sorted[i].Price;
                i++;
            }
            result.Add(last);
        }
        return result.ToArray();
    }

    private static History? LastOnOrBefore(List<History> sortedRows, DateOnly date)
    {
        History? best = null;
        foreach (var r in sortedRows)
        {
            if (r.Date > date) break;
            best = r;
        }
        return best;
    }
}
