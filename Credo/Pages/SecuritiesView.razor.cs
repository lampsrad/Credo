using Credo.Models;
using Credo.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Credo.Pages;

public partial class SecuritiesView
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] GraphService graph { get; set; } = default!;
    [Inject] IJSRuntime jsr { get; set; } = default!;
    [Inject] AppConfig cfg { get; set; } = default!;
    private IList<Security>? Securities { get; set; }
    private IList<Transaction>? Transactions { get; set; }
    private Dictionary<DateOnly, decimal> SpyPrices = new();
    private Security? Security { get; set; }
    bool IsVisibleTransView { get; set; }
    private ChartData? securityChart;
    private bool showChart;
    private int chartWidth;
    private string sortColumn = "Security";
    private string? Title;
    private bool sortAscending = true;


    private void Close()
    {
        Transactions?.Clear();
        IsVisibleTransView = false;
    }
    private void CloseChart()
    {
        showChart = false;
        securityChart = null;
    }
    private void ComputeGainPerc()
    {
        if (Securities is null) return;
        foreach (var sec in Securities)
        {
            var buys = sec.Transactions
                .Where(t => t.TranCode == "by" || t.TranCode == "li")
                .Sum(t => t.LocalAmount ?? 0m);
            if (buys <= 0)
            {
                sec.GainPerc = null;
                continue;
            }
            var sells = sec.Transactions
                .Where(t => t.TranCode == "sl" || t.TranCode == "lo")
                .Sum(t => t.LocalAmount ?? 0m);
            var bought = sec.Transactions
                .Where(t => t.TranCode == "by" || t.TranCode == "li")
                .Sum(t => t.Quantity ?? 0);
            var sold = sec.Transactions
                .Where(t => t.TranCode == "sl" || t.TranCode == "lo")
                .Sum(t => t.Quantity ?? 0);
            var saldo = bought - sold;
            var marketValue = saldo * (sec.Price ?? 0m);
            sec.GainPerc = Math.Round(((marketValue + sells - buys) / buys) * 100, 2);
        }
    }
    private void ComputeGains()
    {
        if (Securities is null) return;
        foreach (var sec in Securities)
        {
            var trs = sec.Transactions.ToList();
            var bought = trs.Where(t => t.TranCode == "by" || t.TranCode == "li").Sum(t => t.Quantity ?? 0);
            var sold = trs.Where(t => t.TranCode == "sl" || t.TranCode == "lo").Sum(t => t.Quantity ?? 0);
            var saldo = bought - sold;
            if (saldo > 0)
            {
                trs.Add(new Transaction
                {
                    TranCode = "cv",
                    TradeDate = DateOnly.FromDateTime(DateTime.Now),
                    LocalAmount = saldo * sec.Price
                });
            }
            decimal local = 0m;
            foreach (var t in trs)
            {
                switch (t.TranCode)
                {
                    case "by":
                    case "li":
                        local -= t.LocalAmount ?? 0m;
                        break;
                    case "lo":
                    case "sl":
                    case "dv":
                    case "in":
                    case "cv":
                        local += t.LocalAmount ?? 0m;
                        break;
                }
            }
            sec.Gain = sec.Currency != "USD=X" ? sec.currency?.Rate * local : local;
        }
    }
    private void ComputeSpyPerf()
    {
        if (Securities is null || SpyPrices.Count == 0) return;
        foreach (var sec in Securities)
        {
            var rate = sec.Currency != "USD=X" ? (sec.currency?.Rate ?? 1m) : 1m;
            var xirr = XirrCalculator.CalculateSpyXirr(sec.Transactions.ToList(), SpyPrices, rate);
            if (!double.IsNaN(xirr))
                sec.SpyPerf = Math.Round((decimal)(xirr * 100), 2);
        }
    }
    private async Task ContextMenuShow(MouseEventArgs e)
    {
        await jsr.InvokeVoidAsync("ShowContextMenu", "Securities", e.ClientX, e.ClientY);
    }
    private async Task ContextMenuHide()
    {
        await jsr.InvokeVoidAsync("HideContextMenu", "Securities");
    }
    private async Task InvestmentsList()
    {
        if (Securities is null) return;
        if (string.IsNullOrEmpty(cfg.DownloadsPath)) return;
        // Negative investments
        var neg = Securities.Where(s => s.Gain < 0).ToList();
        var negativePath = Path.Combine(cfg.DownloadsPath, "Negative-Investments-Exported.csv");
        using var writer = new StreamWriter(negativePath);
        await writer.WriteLineAsync("Ticker,SecurityName,Gain-Loss,IRR");
        foreach (var sec in neg)
        {
            await writer.WriteLineAsync(
                $"{sec.ticker?.Symbol},{sec.SecurityName},{sec.Gain},{sec.XIRR}%");
        }
        // Positive investments
        var pos = Securities.Where(s => s.Gain > 0).ToList();
        var positivePath = Path.Combine(cfg.DownloadsPath, "Positive-Investments-Exported.csv");
        using var writer2 = new StreamWriter(positivePath);
        await writer2.WriteLineAsync("Ticker,SecurityName,Gain-Loss,IRR");
        foreach (var sec in pos)                    // ← Fixed: was using 'neg'
        {
            await writer2.WriteLineAsync(
                $"{sec.ticker?.Symbol},{sec.SecurityName},{sec.Gain},{sec.XIRR}%");
        }
    }
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            jsr.InvokeVoidAsync("sortColumn", "security", 1);
        }
    }
    protected async override Task OnInitializedAsync()
    {
        var spyList = await repo.GetEntitiesNTAsync<History>(x => x.Symbol == "^GSPC");
        SpyPrices = spyList.ToDictionary(s => s.Date, s => s.Price ?? 0m);
        Securities = await repo.GetEntitiesNTAsync<Security>(null);
        ComputeGainPerc();
        ComputeSpyPerf();
    }
    private async Task RecomputeGainsAsync()
    {
        await ContextMenuHide();
        await using var scope = repo.BeginScope();
        Securities = await scope.GetEntitiesAsync<Security>();
        ComputeGains();
        ComputeGainPerc();
        ComputeSpyPerf();
        await scope.SaveChangesAsync();
    }
    private async Task ShowChartAsync(Security s)
    {
        var symbol = s.ticker?.Symbol;
        if (symbol is null) return;
        var data = await graph.LoadTickerDataAsync(symbol, s.Id);
        if (data is null) return;
        securityChart = data with { Title = s.SecurityName };
        chartWidth = await jsr.InvokeAsync<int>("getElementWidth", "members");
        showChart = true;
    }
    private async Task SecurityClicked(MouseEventArgs e, Security security)
    {
        if (Security is not null) Security.Selected = false;
        Security = security;
        Security.Selected = true;
       Transactions =TransactionsClone(security.Transactions);
        string? cur = string.Empty;
        foreach (var t in Transactions)
        {
            int am = t.TranCode switch
            {
                "by" or "li" => -1,
                _ => 1
            };
            t.LocalAmount = (t.LocalAmount ?? 0m) * am;
            int quant = t.TranCode switch
            {
                "sl" or "lo" => -1,
                _ => 1
            };
            t.Quantity = (t.Quantity ?? 0) * quant;
            cur = t.Currency;
        }
        var qtotal = Transactions.Sum(t => t.Quantity);
        if (qtotal > 0)
        {
            Transactions.Add(new Transaction
            {
                LocalAmount = qtotal * security.Price,//Local currency
                TradeDate = DateOnly.FromDateTime(DateTime.Today),
                TranCode = "M-Val"
            });
        }
        var lamount = Transactions.Sum(x => x.LocalAmount);
        if (cur != "USD=X" && security.currency is not null)
            lamount = lamount * security.currency.Rate;
        Transactions.Add(new Transaction
        {
            Quantity = Transactions.Sum(q => q.Quantity),
            TradeDate = DateOnly.FromDateTime(DateTime.Today),
            LocalAmount = lamount,
            Currency="USD",
            TranCode = "Gain"
        });
        Title = security.SecurityName;
        IsVisibleTransView = true;
    }
    private void SortData(string? column = null)
    {
        if (Securities is null) return;
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
        Securities = sortColumn switch
        {
            "Security" => sortAscending
                ? Securities.OrderBy(t => t.SecurityName).ToList()
                : Securities.OrderByDescending(t => t.SecurityName).ToList(),
            "Change" => sortAscending
                ? Securities.OrderBy(t => t.ChangePercent).ToList()
                : Securities.OrderByDescending(t => t.ChangePercent).ToList(),
            "Gain" => sortAscending
          ? Securities.OrderBy(t => t.Gain).ToList()
          : Securities.OrderByDescending(t => t.Gain).ToList(),
            "GainPerc" => sortAscending
          ? Securities.OrderBy(t => t.GainPerc).ToList()
          : Securities.OrderByDescending(t => t.GainPerc).ToList(),
            "XIRR" => sortAscending
                ? Securities.OrderBy(t => t.XIRR).ToList()
                : Securities.OrderByDescending(t => t.XIRR).ToList(),
            "PE" => sortAscending
          ? Securities.OrderBy(t => t.PE).ToList()
          : Securities.OrderByDescending(t => t.PE).ToList(),
            _ => Securities
        };
    }
    private IList<Transaction> TransactionsClone(ICollection<Transaction> transactions)
    {
        var list = new List<Transaction>();
        foreach (var t in transactions)
        {
            list.Add(new Transaction
            {
                ID = t.ID,
                TranCode = t.TranCode,
                SecurityID = t.SecurityID,
                Description = t.Description,
                security = t.security,
                Security = t.Security,
                TradeDate = t.TradeDate,
                Quantity = t.Quantity,
                Currency = t.Currency,
                LocalAmount = t.LocalAmount
            });
        }
        return list;
    }

}

