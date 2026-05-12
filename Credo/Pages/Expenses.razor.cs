using Credo.Models;
using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Credo.Pages;

public partial class Expenses
{
    [Inject] Repo repo { get; set; } = default!;

    private static readonly HashSet<string> ExcludedCodes = ["by", "li", "lo", "sl"];

    private IList<Transaction> allItems = [];
    private IList<Transaction>? Items { get; set; }
    private Dictionary<string, decimal> fxRates = [];
    private decimal Total { get; set; }
    private string sortColumn = "TDate";
    private bool sortAscending = true;
    private string selectedCode = "";
    private IList<string> AvailableCodes { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        var currencies = await repo.GetEntitiesNTAsync<Currency>(null);
        fxRates = currencies
            .Where(c => c.Name != null && c.Rate != 0)
            .ToDictionary(c => c.Name!, c => c.Rate);

        allItems = await repo.GetEntitiesNTAsync<Transaction>(
            t => t.TranCode != null && !ExcludedCodes.Contains(t.TranCode));
        AvailableCodes = allItems
            .Select(t => t.TranCode!)
            .Distinct()
            .Order()
            .ToList();
        ApplyFilter();
    }

    private void OnCodeFilter(ChangeEventArgs e)
    {
        selectedCode = e.Value?.ToString() ?? "";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Items = string.IsNullOrEmpty(selectedCode)
            ? allItems
            : allItems.Where(t => t.TranCode == selectedCode).ToList();
        Total = Items.Sum(t => ToGbp(t.Currency, t.LocalAmount ?? 0));
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

    private decimal ToGbp(string? currency, decimal amount)
    {
        if (currency == null || currency == "GBP") return amount;
        var usd = ToUsd(currency, amount);
        if (fxRates.TryGetValue("GBP", out var gbpRate) && gbpRate != 0)
            return usd / gbpRate;
        return usd;
    }

    private decimal YearLocTotal(IGrouping<int, Transaction> yr) =>
        yr.Sum(t => ToGbp(t.Currency, t.LocalAmount ?? 0));

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
