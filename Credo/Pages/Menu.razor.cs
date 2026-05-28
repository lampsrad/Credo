using Credo.Models;
using Credo.Services;
using Microsoft.AspNetCore.Components;


namespace Credo.Pages;

public partial class Menu
{
    [Inject] Repo repo { get; set; } = default!;
    [Inject] UpdateService updates { get; set; } = default!;
    [Inject] NavigationManager nav { get; set; } = default!;
    [Inject] IHostApplicationLifetime lifetime { get; set; } = default!;
    [Inject] AppConfig cfg { get; set; } = default!;
    IList<string> Headers { get; set; } = new List<string>();


    private void Close()
    {
        cfg.StopBrowser();
        lifetime.StopApplication();
    }
    private async Task ExportSecurities()
    {
        var secs = await repo.GetEntitiesNTAsync<Security>(null);
        var destination = $"{cfg.DownloadsPath}SecuritiesExported.csv";
        using var writer = new StreamWriter(destination);
        await writer.WriteLineAsync("Ticker,SecurityName");
        foreach (var sec in secs)
        {
            var ticker = sec.ticker?.Symbol ?? string.Empty;
            var securityName = sec.SecurityName ?? string.Empty;
            await writer.WriteLineAsync($"{ticker},{securityName}");
        }
    }
    private async Task ExportTickers()
    {
        var tics = await repo.GetEntitiesNTAsync<Ticker>(null);
        var destination = $"{cfg.DownloadsPath}TickersExported.csv";
        using var writer = new StreamWriter(destination);
        await writer.WriteLineAsync("Ticker,SecurityName,Currency");
        foreach (var tic in tics)
        {
            var ticker = tic.Symbol ?? string.Empty;
            var securityName = tic.Name ?? string.Empty;
            var cur = tic.Currency ?? string.Empty;
            await writer.WriteLineAsync($"{ticker},{securityName},{cur}");
        }
    }
    private async Task UpdateSecuritiesXIRR()
    {
        await using (var scope = repo.BeginScope())
        {
            var secs = await scope.GetEntitiesAsync<Security>();
            foreach (var sec in secs)
            {
                var trs = sec.Transactions.ToList();
                if (trs != null)
                {
                    var b = trs.Where(t => t.TranCode == "by" || t.TranCode == "li").Sum(s => s.Quantity);
                    var s = trs.Where(t => t.TranCode == "sl" || t.TranCode == "lo").Sum(s => s.Quantity);
                    var saldo = b - s;
                    if (saldo > 0)
                    {
                        trs.Add(new Transaction
                        {
                            TranCode = "cv",
                            TradeDate = DateOnly.FromDateTime(DateTime.Now),
                            LocalAmount = saldo * sec.Price
                        });
                    }
                    var irr = XirrCalculator.CalculateXirr(trs);
                    if (!double.IsNaN(irr))
                        sec.XIRR = (decimal)irr * 100;
                }
            }
            await scope.SaveChangesAsync();
        }
        nav.NavigateTo("/portfolio");
    }
    private async Task Test()
    {
       // DateOnly start = new DateOnly(2016, 09, 11);
       // var scope = repo.BeginScope();
       // var todelete = await scope.GetEntitiesAsync<History>(h => h.Date < start);
       // scope.RemoveRange(todelete);   
       //int cc = await scope.SaveChangesAsync(); 
    }
}
