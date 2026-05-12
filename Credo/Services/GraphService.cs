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
        var mvRows = await repo.GetEntitiesNTAsync<MarketValue>(null, m => m.Date);
        if (mvRows.Count == 0) return null;

        var labels = mvRows.Select(m => m.Date.ToString("yyyy", CultureInfo.InvariantCulture)).ToArray();
        var data = mvRows.Select(m => m.Value).ToArray();

        var spyRows = await repo.GetEntitiesNTAsync<History>(x => x.Symbol == "^GSPC", s => s.Date);
        decimal?[]? spyData = null;
        if (spyRows.Count > 0)
        {
            spyData = mvRows.Select(m =>
            {
                var match = spyRows.LastOrDefault(s => s.Date <= m.Date.AddMonths(1).AddDays(-1));
                return match is null ? (decimal?)null : match.Price;
            }).ToArray();
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
}
