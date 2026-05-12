using Credo.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Text.Json;

namespace Credo.Pages;

public partial class TransactionsView
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] IJSRuntime jsr { get; set; } = default!;
    [Parameter] public string? Data { get; set; }
    private IList<Transaction>? Transactions { get; set; }
    private IList<Transaction> YearlyFees { get; set; } = new List<Transaction>();
    private string sortColumn = "Security";
    private bool sortAscending = true;

    private async Task ContextMenuHide()
    {
        await jsr.InvokeVoidAsync("HideContextMenu", "Securities");
    }
    private async Task ContextMenuShow(MouseEventArgs e)
    {
        await jsr.InvokeVoidAsync("ShowContextMenu", "Securities", e.ClientX, e.ClientY);
    }
    protected override async Task OnParametersSetAsync()
    {
        if (Data is null)
            Transactions = await repo.GetEntitiesNTAsync<Transaction>(null);
        else
        {
            Transactions = await repo.GetEntitiesNTAsync<Transaction>(x => x.TranCode==Data);
           var Gy = Transactions.GroupBy(x => x.TradeDate.Year);
            foreach(var y in Gy)
            {
                var t = new Transaction
                {
                    TradeDate= new DateOnly(y.Key,12,31),
                    Security= "Management Cost",
                    Currency=y.LastOrDefault()?.Currency,
                    LocalAmount= y.Sum(x=>x.LocalAmount),
                };
                YearlyFees.Add(t);
            }
        }
        SortData();
    }
    private async Task RowClicked(Transaction tr)
    {
        string companyName = tr.Security ?? string.Empty;
        HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        string url = $"https://query2.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(companyName)}&quotesCount=5&newsCount=0";
        HttpResponseMessage response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("quotes", out JsonElement quotes) && quotes.GetArrayLength() > 0)
        {
            // Take the first (usually best) match
            var firstQuote = quotes[0];
            if (firstQuote.TryGetProperty("symbol", out JsonElement symbolElem))
            {
                var symbol = symbolElem.GetString();

            }
        }
    }
    private void SortData(string? column = null)
    {
        if (Transactions is null) return;
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
        Transactions = sortColumn switch
        {
            "Security" => sortAscending
                ? Transactions.OrderBy(t => t.Security).ToList()
                : Transactions.OrderByDescending(t => t.Security).ToList(),
            "Code" => sortAscending
                ? Transactions.OrderBy(t => t.TranCode).ToList()
                : Transactions.OrderByDescending(t => t.TranCode).ToList(),
            "TDate" => sortAscending
                ? Transactions.OrderBy(t => t.TradeDate).ToList()
                : Transactions.OrderByDescending(t => t.TradeDate).ToList(),
            _ => Transactions
        };
    }

}
