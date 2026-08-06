using System.Globalization;
using System.Text.Json;
using Credo.Models;
using Credo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using YahooQuotesApi;
using History = Credo.Models.History;

namespace Credo.Pages;

public partial class WatchlistView
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] GraphService graph { get; set; } = default!;
    [Inject] UpdateService updates { get; set; } = default!;
    [Inject] IJSRuntime jsr { get; set; } = default!;

    private IList<Watchlist>? Items { get; set; }
    private Dictionary<string, (decimal? Price, DateOnly Date)> LatestPrices { get; set; } = new();
    private Dictionary<string, decimal?> PrevDayPrices { get; set; } = new();
    /// <summary>Extended-hours price from Yahoo price module (pre preferred in PRE, else post).</summary>
    private Dictionary<string, decimal> ExtHoursPrices { get; set; } = new();

    private string newSymbol = string.Empty;
    private string? addError;
    private bool isAdding;
    private bool isUpdating;

    private ChartData? securityChart;
    private bool showChart;
    private int chartWidth;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Items = await repo.GetEntitiesNTAsync<Watchlist>(null);
        LatestPrices = new();
        PrevDayPrices = new();
        ExtHoursPrices = new();
        if (Items.Count == 0) return;

        var symbols = Items
            .Where(w => w.Symbol is not null)
            .Select(w => w.Symbol!)
            .ToList();

        var allHistory = await repo.GetEntitiesNTAsync<History>(
            h => h.Symbol != null && symbols.Contains(h.Symbol));

        foreach (var g in allHistory.GroupBy(h => h.Symbol!))
        {
            var ordered = g.OrderByDescending(h => h.Date).ToList();
            LatestPrices[g.Key] = (ordered[0].Price, ordered[0].Date);
            if (ordered.Count > 1)
                PrevDayPrices[g.Key] = ordered[1].Price;
        }

        await LoadExtHoursAsync(symbols);
    }

    /// <summary>
    /// Fetches Yahoo <c>price</c> module per symbol (snapshot pre/post fields are unreliable).
    /// Soft-fails so History columns still render.
    /// </summary>
    private async Task LoadExtHoursAsync(IList<string> symbols)
    {
        try
        {
            var yahoo = new YahooQuotesBuilder().Build();
            using var gate = new SemaphoreSlim(4);
            var tasks = symbols.Select(async sym =>
            {
                await gate.WaitAsync();
                try
                {
                    var result = await yahoo.GetModulesAsync(sym, new[] { "price" });
                    if (!result.HasValue) return (sym, (decimal?)null);

                    foreach (var prop in result.Value)
                    {
                        if (prop.Name != "price" || prop.Value.ValueKind != JsonValueKind.Object)
                            continue;
                        return (sym, PickExtHoursPrice(prop.Value));
                    }
                    return (sym, (decimal?)null);
                }
                catch { return (sym, (decimal?)null); }
                finally { gate.Release(); }
            });

            foreach (var (sym, price) in await Task.WhenAll(tasks))
            {
                if (price is > 0)
                    ExtHoursPrices[sym] = price.Value;
            }
        }
        catch { /* leave ExtHoursPrices empty */ }
    }

    /// <summary>PRE session prefers pre-market (falls back to post); otherwise post (falls back to pre).</summary>
    private static decimal? PickExtHoursPrice(JsonElement price)
    {
        var state = price.TryGetProperty("marketState", out var ms) && ms.ValueKind == JsonValueKind.String
            ? (ms.GetString() ?? "").ToUpperInvariant()
            : "";

        var pre = Positive(ReadYahooRaw(price, "preMarketPrice"));
        var post = Positive(ReadYahooRaw(price, "postMarketPrice"));

        return state is "PRE" or "PREPRE"
            ? pre ?? post
            : post ?? pre;
    }

    private static decimal? Positive(decimal? v) => v is > 0 ? v : null;

    /// <summary>Yahoo module field: number, or <c>{ "raw": n }</c>; empty object → null.</summary>
    private static decimal? ReadYahooRaw(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("raw", out var raw)
            && raw.ValueKind == JsonValueKind.Number)
            return raw.GetDecimal();
        return null;
    }

    private static string PctClass(decimal? v) =>
        v is null ? "" : v > 0 ? "text-success" : v < 0 ? "text-danger" : "";

    private static string FormatSignedPct2(decimal? v) =>
        v is null ? "—" : string.Format(CultureInfo.InvariantCulture, "{0:+#,##0.00;-#,##0.00;0.00}%", v);

    private static decimal? PctChange(decimal? latest, decimal? prev) =>
        latest is null || prev is null || prev == 0
            ? null
            : (latest.Value - prev.Value) / prev.Value * 100m;

    private async Task AddAsync()
    {
        var sym = newSymbol.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(sym)) return;
        addError = null;
        isAdding = true;
        try
        {
            var existing = await repo.GetEntityNTAsync<Watchlist>(w => w.Symbol == sym);
            if (existing is not null)
            {
                addError = $"{sym} is already on the watchlist.";
                return;
            }

            string name;
            try
            {
                var snapshots = await new YahooQuotesBuilder().Build()
                    .GetSnapshotAsync(new[] { sym });
                name = snapshots.TryGetValue(sym, out var snap) && snap is not null
                    ? snap.LongName ?? snap.ShortName ?? sym
                    : sym;
            }
            catch { name = sym; }

            await using var scope = repo.BeginScope();
            scope.Add(new Watchlist { Symbol = sym, Name = name });
            await scope.SaveChangesAsync();

            newSymbol = string.Empty;
            await LoadAsync();
        }
        finally { isAdding = false; }
    }

    private async Task RemoveAsync(Watchlist item)
    {
        await using var scope = repo.BeginScope();
        var entity = await scope.GetEntityAsync<Watchlist>(w => w.Id == item.Id);
        if (entity is not null)
        {
            scope.RemoveRange(new List<Watchlist> { entity });
            await scope.SaveChangesAsync();
        }
        await LoadAsync();
    }

    private async Task ShowChartAsync(Watchlist item)
    {
        if (item.Symbol is null) return;
        var data = await graph.LoadAdhocTickerDataAsync(item.Symbol);
        if (data is null) return;
        securityChart = data;
        chartWidth = await jsr.InvokeAsync<int>("getViewportChartWidth");
        showChart = true;
    }

    private void CloseChart()
    {
        showChart = false;
        securityChart = null;
    }

    private async Task UpdateHistoryAsync()
    {
        isUpdating = true;
        try { await updates.UpdateWatchlistHistoryAsync(); }
        finally
        {
            isUpdating = false;
            await LoadAsync();
        }
    }
}
