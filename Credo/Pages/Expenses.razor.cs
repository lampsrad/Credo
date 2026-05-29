using Credo.Models;
using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Credo.Pages;

public partial class Expenses
{
    [Inject] Repo repo { get; set; } = default!;

    private static readonly HashSet<string> ExcludedCodes = ["by", "sl", "li" , "dv", "in"];

    private IList<Transaction> allItems = [];
    private IList<Transaction>? Items { get; set; }
    private Dictionary<string, decimal> fxRates = [];
    private string sortColumn = "TDate";
    private bool sortAscending = true;
    private HashSet<string> selectedCodes = [];
    private bool dropdownOpen = false;
    private IList<string> AvailableCodes { get; set; } = [];
    private string FilterLabel => selectedCodes.Count == 0 ? "All" : string.Join(", ", selectedCodes.Order());

    protected override async Task OnInitializedAsync()
    {
        var currencies = await repo.GetEntitiesNTAsync<Currency>(null);
        fxRates = currencies
            .Where(c => c.Name != null && c.Rate != 0)
            .ToDictionary(c => c.Name!, c => c.Rate);

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
        Items = selectedCodes.Count == 0
            ? allItems
            : allItems.Where(t => t.TranCode != null && selectedCodes.Contains(t.TranCode)).ToList();
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
