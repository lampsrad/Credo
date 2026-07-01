using Credo.Models;
using Credo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Globalization;

namespace Credo.Pages;

public partial class PortfolioView
{
    [Inject] AppConfig config { get; set; }
    [Inject] UpdateService updates { get; set; } = default!;
    [Inject] Repo repo { get; set; } = default!;
    [Inject] GraphService graph { get; set; } = default!;
    [Inject] IJSRuntime jsr { get; set; } = default!;

    IList<Portfolio>? Portfs { get; set; }
    private Portfolio? selectedRow;
    private ChartData? securityChart;
    private bool showChart;
    private int chartWidth;
    private static bool IsUpdated;
    private string sortColumn = "Security";
    private bool IsVisibleTotal, sortAscending = true;
    private bool isRefreshing;
    private bool hasRefreshed;
    private decimal? portfolioCostBase;
    private DateTime? lastUpdated { get; set; }
    private HashSet<int> checkedRows = new();
    private int? editingRowId;
    private bool isSavingQty;
    private bool qtySaveFromEnter;
    private bool pendingQtyFocus;
    private int? savedRowId;
    private Dictionary<int, int?> editQty = new();

    private void ToggleChecked(int id)
    {
        if (!checkedRows.Add(id))
            checkedRows.Remove(id);
    }

    private HashSet<int> GetExcludedSecurityIds() =>
        Items
            .Where(p => checkedRows.Contains(p.Id) && p.SecurityID.HasValue)
            .Select(p => p.SecurityID!.Value)
            .ToHashSet();

    private static readonly Dictionary<string, bool> DefaultAscending = new()
    {
        ["Security"] = true,
        ["Change"] = false,
        ["MarketValue"] = false,
        ["Gain"] = false,
        ["GainPercentage"] = false,
        ["IRR"] = false,
        ["Percentage"] = false,
    };

    private string AriaSort(string column) =>
    sortColumn == column ? (sortAscending ? "ascending" : "descending") : "none";
    private async Task ContextMenuShow(MouseEventArgs e) =>
    await jsr.InvokeVoidAsync("ShowContextMenu", "Securities", e.ClientX, e.ClientY);
    private async Task ContextMenuHide() =>
        await jsr.InvokeVoidAsync("HideContextMenu", "Securities");
    private IList<Portfolio> Items => Portfs ?? Array.Empty<Portfolio>();
    private decimal TotalMarketValue => Items.Sum(p => p.Market_Value ?? 0m);
    private decimal totalYesterday => Items.Sum(p =>  p.Market_Value_Yesterday ?? 0m);
    private decimal TotalGainDay => Items.Sum(p => (p.Market_Value ?? 0m) - (p.Market_Value_Yesterday ?? 0m));
    private decimal TotalCost => Items.Sum(p => p.Cost ?? 0m);
    private decimal TotalGain => Items.Sum(p => p.Gain ?? 0m);
    private static string FormatPrice(Portfolio p)
    {
        if (p.Price is null) return "";
        var code = p.security?.Currency ?? p.Currency;
        var symbol = code switch
        {
            "USD=X" or "USD" => "$",
            "GBP=X" or "GBP" => "£",
            "EUR=X" or "EUR" => "€",
            "DKK=X" or "DKK" => "Kr.",
            "CHF=X" or "CHF" => "Fr.",
            "CAD=X" or "CAD" => "CA$",
            "ZAR=X" or "ZAR" => "R",
            "TWD=X" or "TWD" => "NT$",
            _ => p.currency?.Symbol ?? p.currency?.Name ?? ""
        };
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:N2}", symbol, p.Price);
    }
    private static string FormatSignedPct1(decimal? v) =>
    v is null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:+#,##0.0;-#,##0.0;0.0}%", v);
    private static string FormatSignedPct2(decimal? v) =>
        v is null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:+#,##0.00;-#,##0.00;0.00}%", v);
    private static string FormatSignedPctInt(decimal? v) =>
        v is null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:+#,##0;-#,##0;0}%", v);
    private static string FormatSignedMoney(decimal? v) =>
        v is null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:+$#,##0.00;-$#,##0.00;$0.00}", v);
    private static string FormatRelative(DateTime when)
    {
        var diff = DateTime.Now - when;
        if (IsUpdated==false) return "Not Yet";
        if (diff.TotalMinutes < 1) return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        return when.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
    }
    protected async override Task OnInitializedAsync()
    {
        isRefreshing = true;
        try
        {
            Portfs = await repo.GetEntitiesNTAsync<Portfolio>();
            lastUpdated = DateTime.Now;
            StateHasChanged();
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private void RowClickedAsync(Portfolio p)
    {
        selectedRow = p;
    }
    private async Task ShowChartAsync(Portfolio p)
    {
        var symbol = p.security?.ticker?.Symbol;
        if (symbol is null) return;
        var data = await graph.LoadTickerDataAsync(symbol, p.SecurityID);
        if (data is null) return;
        securityChart = data with { Title = p.Security_Description };
        portfolioCostBase = null;
        chartWidth = await jsr.InvokeAsync<int>("getViewportChartWidth");
        showChart = true;
    }
    private async Task ShowPortfolioChartAsync()
    {
        var excludedIds = GetExcludedSecurityIds();
        var data = excludedIds.Count == 0
            ? await graph.LoadTickerDataAsync("Portfolio", null)
            : await graph.ComputePortfolioChartAsync(excludedIds);
        if (data is null) return;
        securityChart = data with { Title = "Portfolio" };
        portfolioCostBase = 1_339_055m;
        chartWidth = await jsr.InvokeAsync<int>("getViewportChartWidth");
        showChart = true;
    }
    private void CloseChart()
    {
        showChart = false;
        securityChart = null;
        portfolioCostBase = null;
    }
    private static string PctClass(decimal? v) =>
    v is null ? "" : (v > 0 ? "text-success" : (v < 0 ? "text-danger" : ""));
    private async Task RefreshAsync()
    {
        await ContextMenuHide();
        IsUpdated = true;
        isRefreshing = true;
        try
        {
            await updates.UpdateSecuritiesAsync();
            Portfs = await repo.GetEntitiesNTAsync<Portfolio>(null);
            lastUpdated = DateTime.Now;
            StateHasChanged();
        }
        finally
        {
            isRefreshing = false;
        }
    }
    private void ToggleColumns()
    {
        hasRefreshed = !hasRefreshed;
        if (hasRefreshed)
            selectedRow = null;
    }
    private void SortData(string? column = null)
    {
        if (Portfs is null) return;
        if (column != null)
        {
            if (sortColumn == column)
            {
                sortAscending = !sortAscending;
            }
            else
            {
                sortColumn = column;
                sortAscending = DefaultAscending.TryGetValue(column, out var d) ? d : true;
            }
        }
        Portfs = sortColumn switch
        {
            "Security" => sortAscending
                ? Portfs.OrderBy(p => p.Security_Description).ToList()
                : Portfs.OrderByDescending(p => p.Security_Description).ToList(),
            "Gain" => sortAscending
                ? Portfs.OrderBy(t => t.Gain).ToList()
                : Portfs.OrderByDescending(t => t.Gain).ToList(),
            "GainPercentage" => sortAscending
                ? Portfs.OrderBy(t => t.GainPerc).ToList()
                : Portfs.OrderByDescending(t => t.GainPerc).ToList(),
            "Change" => sortAscending
                ? Portfs.OrderBy(t => t.security?.ChangePercent).ToList()
                : Portfs.OrderByDescending(t => t.security?.ChangePercent).ToList(),
            "CAGR" => sortAscending
                ? Portfs.OrderBy(t => t.IRR).ToList()
                : Portfs.OrderByDescending(t => t.IRR).ToList(),
            "MarketValue" => sortAscending
                ? Portfs.OrderBy(t => t.Market_Value).ToList()
                : Portfs.OrderByDescending(t => t.Market_Value).ToList(),
            "Percentage" => sortAscending
                ? Portfs.OrderBy(t => t.Pct).ToList()
                : Portfs.OrderByDescending(t => t.Pct).ToList(),
            _ => Portfs
        };
    }
    private string SortGlyph(string column) =>
        sortColumn == column ? (sortAscending ? "▲" : "▼") : "↕";
    private void ToggleTotalVisibility() => IsVisibleTotal = !IsVisibleTotal;

    private void StartEditQty(Portfolio p)
    {
        editingRowId = p.Id;
        editQty[p.Id] = p.Quantity;
        savedRowId = null;
        pendingQtyFocus = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!pendingQtyFocus || editingRowId is not int id) return;
        pendingQtyFocus = false;
        await jsr.InvokeVoidAsync("FocusElement", $"qty-input-{id}");
    }

    private void CancelEditQty()
    {
        if (editingRowId is int id)
            editQty.Remove(id);
        editingRowId = null;
    }

    private int? GetEditQty(Portfolio p) =>
        editQty.TryGetValue(p.Id, out var qty) ? qty : p.Quantity;

    private void OnQtyInput(Portfolio p, ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) && qty >= 0)
            editQty[p.Id] = qty;
    }

    private async Task OnQtyKeyDown(Portfolio p, KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            qtySaveFromEnter = true;
            try
            {
                await SaveQuantityAsync(p);
            }
            finally
            {
                qtySaveFromEnter = false;
            }
        }
        else if (e.Key == "Escape")
            CancelEditQty();
    }

    private async Task OnQtyBlurAsync(Portfolio p)
    {
        if (qtySaveFromEnter) return;
        await SaveQuantityAsync(p);
    }

    private async Task SaveQuantityAsync(Portfolio p)
    {
        if (editingRowId != p.Id || isSavingQty) return;
        var newQty = GetEditQty(p);
        if (newQty == p.Quantity)
        {
            CancelEditQty();
            return;
        }
        if (newQty < 0) return;

        isSavingQty = true;
        try
        {
            await using var scope = repo.BeginScope();
            var entity = await scope.GetEntityAsync<Portfolio>(x => x.Id == p.Id);
            if (entity is null) return;
            entity.Quantity = newQty;
            await scope.SaveChangesAsync();
            await updates.UpdatePortfolioAsync();
            Portfs = await repo.GetEntitiesNTAsync<Portfolio>();
            editQty.Remove(p.Id);
            editingRowId = null;
            savedRowId = p.Id;
            hasRefreshed = true;
            SortData();
            StateHasChanged();
            await Task.Delay(1200);
            if (savedRowId == p.Id)
            {
                savedRowId = null;
                StateHasChanged();
            }
        }
        finally
        {
            isSavingQty = false;
        }
    }
}
