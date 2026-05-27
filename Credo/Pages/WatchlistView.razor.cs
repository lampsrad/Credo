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

    private string newSymbol = string.Empty;
    private string newName = string.Empty;
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
        if (Items.Count == 0) return;

        var symbols = Items
            .Where(w => w.Symbol is not null)
            .Select(w => w.Symbol!)
            .ToList();

        var allHistory = await repo.GetEntitiesNTAsync<History>(
            h => h.Symbol != null && symbols.Contains(h.Symbol));

        LatestPrices = allHistory
            .GroupBy(h => h.Symbol!)
            .ToDictionary(
                g => g.Key,
                g => { var row = g.OrderByDescending(h => h.Date).First(); return (row.Price, row.Date); });
    }

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

            var name = newName.Trim();
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    var snapshots = await new YahooQuotesBuilder().Build()
                        .GetSnapshotAsync(new[] { sym });
                    if (snapshots.TryGetValue(sym, out var snap) && snap is not null)
                        name = snap.LongName ?? snap.ShortName ?? sym;
                    else
                        name = sym;
                }
                catch { name = sym; }
            }

            await using var scope = repo.BeginScope();
            scope.Add(new Watchlist { Symbol = sym, Name = name });
            await scope.SaveChangesAsync();

            newSymbol = string.Empty;
            newName = string.Empty;
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
            StateHasChanged();
        }
    }
}
