using Credo.Models;
using System.Globalization;

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
        decimal?[]? spyData = null;
        if (spyRows.Count > 0)
        {
            var spyDict = spyRows.ToDictionary(s => s.Date, s => s.Price);
            spyData = mvRows.Select(h => spyDict.TryGetValue(h.Date, out var p) ? p : null).ToArray();
        }

        return new ChartData(labels, data, spyData, null, null, null);
    }

    public async Task<ChartData?> LoadTickerDataAsync(string symbol, int? secId)
    {
        var historyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == symbol, h => h.Date);
        if (historyRows.Count == 0) return null;
        var labels = historyRows.Select(h => h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var data = historyRows.Select(h => h.Price ?? 0m).ToArray();
        var spyRows = await repo.GetEntitiesNTAsync<History>(h => h.Symbol == "^GSPC", h => h.Date);
        var spyDict = spyRows.ToDictionary(s => s.Date, s => s.Price);
        var spyData = historyRows.Select(h => spyDict.TryGetValue(h.Date, out var p) ? p : null).ToArray();
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
