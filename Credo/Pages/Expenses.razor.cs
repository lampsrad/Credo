using Credo.Models;
using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Credo.Pages;

public partial class Expenses : IDisposable
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] State state { get; set; } = default!;

    private static readonly HashSet<string> ExcludedCodes = ["by", "sl", "li", "dv", "in", "wd"];

    private IList<Transaction> allItems = [];
    private IList<Transaction>? Items { get; set; }
    private Dictionary<string, decimal> fxRates = [];
    private string sortColumn = "TDate";
    private bool sortAscending = true;
    private HashSet<string> selectedCodes = [];
    private bool dropdownOpen = false;
    private IList<string> AvailableCodes { get; set; } = [];
    // Invested value for a trade month: Portfolio history minus that month's CASH history.
    private Dictionary<(int Year, int Month), decimal> monthBases = [];
    private string FilterLabel => selectedCodes.Count == 0 ? "All" : string.Join(", ", selectedCodes.Order());

    protected override void OnInitialized()
    {
        state.YearChanged += OnYearChanged;
    }
    public void Dispose()
    {
        state.YearChanged -= OnYearChanged;
    }
    private void OnYearChanged() => _ = InvokeAsync(() =>
    {
        ApplyFilter();
        StateHasChanged();
    });
    protected override async Task OnInitializedAsync()
    {
        var currencies = await repo.GetEntitiesNTAsync<Currency>(null);
        fxRates = currencies
            .Where(c => c.Name != null && c.Rate != 0)
            .ToDictionary(c => c.Name!, c => c.Rate);

        await LoadMonthBasesAsync();

        allItems = await repo.GetEntitiesNTAsync<Transaction>(
            t => t.TranCode != null &&t.SecurityID==null && !ExcludedCodes.Contains(t.TranCode));
        AvailableCodes = allItems
            .Select(t => t.TranCode!)
            .Distinct()
            .Order()
            .ToList();
        ApplyFilter();
    }
    private void ToggleDropdown() => dropdownOpen = !dropdownOpen;
    private void ToggleCode(string code)
    {
        if (!selectedCodes.Remove(code))
            selectedCodes.Add(code);
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        IEnumerable<Transaction> rows = selectedCodes.Count == 0
            ? allItems
            : allItems.Where(t => t.TranCode != null && selectedCodes.Contains(t.TranCode));
        if (state.Year is int year)
            rows = rows.Where(t => t.TradeDate.Year == year);
        Items = rows.ToList();
        SortData();
    }
    // Rate is stored as USD per 1 unit of the currency.
    // e.g. GBP rate ≈ 1.27 → £1 = $1.27;  USD rate = 1.
    private decimal ToUsd(string? currency, decimal amount)
    {
        if (currency == null || currency == "USD") return amount;
        if (fxRates.TryGetValue(currency, out var rate) && rate != 0)
            return amount * rate;
        return amount;
    }
    // Portfolio history includes cash. Pair it with the CASH row for that month (dated the 25th).
    // Use the portfolio price on that date, otherwise the latest stored day in the month on or before it.
    // If every stored day in the month is later, use the earliest.
    private async Task LoadMonthBasesAsync()
    {
        var rows = await repo.GetEntitiesNTAsync<History>(
            h => h.Symbol == "Portfolio" || h.Symbol == "CASH");
        var portfolio = rows
            .Where(h => h.Symbol == "Portfolio" && h.Price != null)
            .GroupBy(h => (h.Date.Year, h.Date.Month))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in rows.Where(h => h.Symbol == "CASH" && h.Price != null).GroupBy(h => (h.Date.Year, h.Date.Month)))
        {
            var cash = group.FirstOrDefault(h => h.Date.Day == 25) ?? group.MaxBy(h => h.Date);
            if (cash?.Price is not decimal cashPrice) continue;
            if (!portfolio.TryGetValue(group.Key, out var days)) continue;
            var chosen = days.Where(d => d.Date <= cash.Date).MaxBy(d => d.Date) ?? days.MinBy(d => d.Date);
            if (chosen?.Price is not decimal portPrice) continue;
            var basis = portPrice - cashPrice;
            if (basis != 0)
                monthBases[group.Key] = basis;
        }
    }
    private decimal? LineUsd(Transaction t) =>
        t.LocalAmount.HasValue ? ToUsd(t.Currency, t.LocalAmount.Value) : null;
    private decimal SumUsd(IEnumerable<Transaction> rows) =>
        rows.Sum(t => LineUsd(t) ?? 0m);
    // One stored row is a single charge. Glacier and management are monthly, custody uses a factor of 3.
    private static int AnnualFactor(Transaction t)
    {
        var name = t.Security;
        if (string.IsNullOrWhiteSpace(name)) return 1;
        if (name.Contains("Glacier Fees", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Management Fee", StringComparison.OrdinalIgnoreCase))
            return 12;
        if (name.Contains("Custody Fee", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 1;
    }
    private decimal? AnnualUsd(Transaction t)
    {
        var usd = LineUsd(t);
        return usd is null ? null : usd.Value * AnnualFactor(t);
    }
    private string FormatPerc(decimal? usd, DateOnly date)
    {
        if (usd is null || !monthBases.TryGetValue((date.Year, date.Month), out var basis)) return "";
        return string.Format(CultureInfo.InvariantCulture, "{0:N2}%", usd.Value / basis * 100m);
    }
    // Year and grand totals stay on the statement USD. Each row uses its own month's invested value.
    private string FormatGroupPerc(IEnumerable<Transaction> rows)
    {
        decimal sum = 0;
        var any = false;
        foreach (var t in rows)
        {
            var usd = LineUsd(t);
            if (usd is null || !monthBases.TryGetValue((t.TradeDate.Year, t.TradeDate.Month), out var basis))
                continue;
            sum += usd.Value / basis * 100m;
            any = true;
        }
        return any ? string.Format(CultureInfo.InvariantCulture, "{0:N2}%", sum) : "";
    }
    private static CultureInfo CultureFor(string? currency) => currency switch
    {
        "USD" => new CultureInfo("en-US"),
        "EUR" => new CultureInfo("en-IE"),
        "GBP" => new CultureInfo("en-GB"),
        "DKK" => new CultureInfo("da-DK"),
        "CHF" => new CultureInfo("fr-CH"),
        "HKD" => new CultureInfo("en-HK"),
        _ => new CultureInfo("en-US")
    };
    private void SortData(string? column = null)
    {
        if (Items is null) return;
        if (column != null)
        {
            if (sortColumn == column)
                sortAscending = !sortAscending;
            else
            {
                sortColumn = column;
                sortAscending = true;
            }
        }
        Items = sortColumn switch
        {
            "Code" => sortAscending
                ? Items.OrderBy(t => t.TranCode).ToList()
                : Items.OrderByDescending(t => t.TranCode).ToList(),
            "Security" => sortAscending
                ? Items.OrderBy(t => t.Security).ToList()
                : Items.OrderByDescending(t => t.Security).ToList(),
            _ => sortAscending
                ? Items.OrderBy(t => t.TradeDate).ToList()
                : Items.OrderByDescending(t => t.TradeDate).ToList(),
        };
    }
}
