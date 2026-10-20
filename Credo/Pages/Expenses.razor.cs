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
    private decimal investedBase;
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

        var portfolios = await repo.GetEntitiesNTAsync<Portfolio>(null);
        investedBase = portfolios.Where(p => !IsCash(p)).Sum(p => p.Market_Value ?? 0m);

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
    // Cash nominee lines and cash FX (symbol ending =X). US stocks also use currency USD=X, so currency is not the test.
    private static bool IsCash(Portfolio p)
    {
        var symbol = p.security?.ticker?.Symbol;
        if (symbol != null && symbol.EndsWith("=X", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = p.Security_Description;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.StartsWith("US DOLLAR", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("POUND STERLING", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("EURO", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AUSTRALIAN DOLLAR", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CANADIAN DOLLAR", StringComparison.OrdinalIgnoreCase);
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
    private string FormatPerc(decimal? annualUsd)
    {
        if (annualUsd is null || investedBase == 0) return "";
        return string.Format(CultureInfo.InvariantCulture, "{0:N2}%", annualUsd.Value / investedBase * 100m);
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
