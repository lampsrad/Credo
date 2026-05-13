using Credo.Classes;
using Credo.Models;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Credo.Pages;

public partial class ImportView
{
    [Inject] NavigationManager nav { get; set; } = default!;
    [Inject] State state { get; set; } = default!;
    [Inject] Repo repo { get; set; } = default!;
    [Inject] ILogger<ImportView> logger { get; set; } = default!;
    [Inject] AppConfig cfg { get; set; } = default!;


    private string Title = "IMPORTED";
    private bool b1, b2, b3, b4, b5, b6;
    private Models.Results results = new();

    private CsvConfiguration CsvConfig()
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = ",",
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            BadDataFound = null,
            MissingFieldFound = null,
        };
        return config;
    }
    private string DateRegex(string date)
    {
        string pattern = @"\b(\d{1,2})/(\d{1,2})/(\d{2,4})\b";
        string result = Regex.Replace(date, pattern, m =>
        {
            string day = m.Groups[1].Value.PadLeft(2, '0');
            string month = m.Groups[2].Value.PadLeft(2, '0');
            string year = m.Groups[3].Value.Length == 2 ? "20" + m.Groups[3].Value : m.Groups[3].Value;
            return $"{year}-{month}-{day}";
        });
        return result;
    }
    private static string MapCurrencyName(string name)
    {
        Match m1 = Regex.Match(name, @"\b\w+\b");
        return m1.Value.ToLowerInvariant() switch
        {
            "australian" => "USD",
            "euro" => "EUR",
            "pound" => "GBP",
            "us" => "USD",
            _ => "USD"
        };
    }
    private async Task ImportCurrencies()
    {
        b2 = true;
        if (string.IsNullOrEmpty(cfg.DownloadsPath))
        {
            await ShowMessage(0);
            return;
        }
        var exfile = Directory.GetFiles(cfg.DownloadsPath, "Currencies*.csv").FirstOrDefault();
        if (exfile is null)
        {
            await ShowMessage(0);
            return;
        }
        using var reader = new StreamReader(exfile);
        using var csv = new CsvReader(reader, CsvConfig());
        csv.Context.RegisterClassMap<CurrencyMap>();
        await using var scope = repo.BeginScope();
        await foreach (var rec in csv.GetRecordsAsync<Currency>())
        {
            var cur = await scope.GetEntityAsync<Currency>(c => c.Symbol == rec.Symbol);
            if (cur == null)
            {
                rec.Rate = rec.Name == "USD" ? 1 : rec.Rate;
                scope.Add(rec);
            }
            else
            {
                cur.Symbol = rec.Symbol;
                cur.Name = rec.Name;
            }
        }
        int cc = await scope.SaveChangesAsync();
        await ShowMessage(cc);
    }
    private async Task ImportPortfolio()
    {
        try
        {
            b5 = true;
            if (string.IsNullOrEmpty(cfg.DownloadsPath))
            {
                await ShowMessage(0);
                return;
            }
            var exfile = Directory.GetFiles(cfg.DownloadsPath, "Portfolio*.csv").FirstOrDefault();
            if (exfile is null)
            {
                await ShowMessage(0);
                return;
            }
            using var reader = new StreamReader(exfile);
            using var csv = new CsvReader(reader, CsvConfig());
            csv.Context.RegisterClassMap<PortfolioMap>();

            await using var scope = repo.BeginScope();
            var currencyByName = (await scope.GetEntitiesAsync<Currency>())
                .Where(c => c.Name is not null)
                .ToDictionary(c => c.Name!, c => c.ID);
            var existingPortfs = (await scope.GetEntitiesAsync<Portfolio>())
                .Where(p => p.Security_Description is not null)
                .ToDictionary(p => p.Security_Description!, p => p);
            var portfs = new List<Portfolio>();
            await foreach (var port in csv.GetRecordsAsync<Portfolio>())
            {
                if (port.Market_Value == 0 || port.Security_Description == "CONSTELLATION SOFTWARE-WT 40")
                    continue;
                if (existingPortfs.TryGetValue(port.Security_Description ?? string.Empty, out var existing))
                {
                    if (port.Unit_Cost == port.Price)
                        existing.Quantity = port.Quantity;
                    continue;
                }
                var sec = await repo.GetEntityNTAsync<Security>(s => s.SecurityName == port.Security_Description);
                port.SecurityID = sec?.Id;
                port.CurrencyID = sec?.CurrencyID;
                port.Price = sec?.Price;
                if (sec == null)
                {
                    var curName = MapCurrencyName(port.Security_Description ?? string.Empty);
                    port.CurrencyID = currencyByName.TryGetValue(curName, out var id) ? id : (int?)null;
                    port.Price = 1;
                }
                portfs.Add(port);
            }
            scope.AddRange(portfs);
            var cc = await scope.SaveChangesAsync();
            await ShowMessage(cc);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportPortfolio failed");
            await ShowMessage(0);
        }
    }
    private async Task ImportSecurities()
    {
        b4 = true;
        await using var scope = repo.BeginScope();

        var ticks = await scope.GetEntitiesAsync<Ticker>();
        var currencies = (await scope.GetEntitiesAsync<Currency>())
            .Where(c => c.Name is not null)
            .ToDictionary(c => c.Name!, c => c.ID);
        var secs = ticks.Select(t => new Security
        {
            TickerID = t.ID,
            CurrencyID = t.Currency is not null && currencies.TryGetValue(t.Currency, out var curId) ? curId : (int?)null,
            SecurityName = t.Name
        }).ToList();
        scope.AddRange(secs);
        int cc = await scope.SaveChangesAsync();

        var secNames = secs.Select(s => s.SecurityName).ToList();
        var transByName = (await scope.GetEntitiesAsync<Transaction>(t => secNames.Contains(t.Security)))
            .Where(t => t.Security is not null)
            .GroupBy(t => t.Security!)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var s in secs)
        {
            if (s.SecurityName is null) continue;
            if (!transByName.TryGetValue(s.SecurityName, out var trans)) continue;
            foreach (var t in trans)
                t.SecurityID = s.Id;
        }
        await scope.SaveChangesAsync();
        await ShowMessage(cc);
    }
    private async Task ImportTransactions()
    {
        b3 = true;
        int cc = await ImportTransactionsAsync();
        await ShowMessage(cc);
    }
    public async Task<int> ImportTransactionsAsync()
    {
        if (string.IsNullOrEmpty(cfg.DownloadsPath)) return 0;
        var exfile = Directory.GetFiles(cfg.DownloadsPath, "Transaction*.csv").FirstOrDefault();
        if (exfile is null) return 0;
        using var reader = new StreamReader(exfile);
        using var csv = new CsvReader(reader, CsvConfig());

        // The CSV has preamble rows; scan until we find the real header row.
        bool headerFound = false;
        while (csv.Read())
        {
            if (csv.GetField(1)?.Trim() == "Tran Code")
            {
                csv.ReadHeader();
                headerFound = true;
                break;
            }
        }
        if (!headerFound) return 0;

        await using var scope = repo.BeginScope();
        var existing = (await scope.GetEntitiesAsync<Transaction>())
            .Select(t => (t.TradeDate, t.LocalAmount))
            .ToHashSet();
        var transactions = new List<Transaction>();
        while (csv.Read())
        {
            var trancode = csv.GetField("Tran Code");
            if (string.IsNullOrEmpty(trancode) || trancode.Length > 4)
                continue;
            var rawDate = csv.GetField("Trade Date");
            if (string.IsNullOrWhiteSpace(rawDate)) continue;
            var tradedate = DateRegex(DateRegex(rawDate));
            if (!DateOnly.TryParse(tradedate, out var parsedDate)) continue;
            // Strip spaces used as thousands separators and trailing periods before parsing.
            var qfield = csv.GetField("Quantity")?.Replace(" ", "").Trim().TrimEnd('.');
            var rawAmt = csv.GetField("Local Amount")?.Replace(" ", "");
            var localAmt = decimal.TryParse(rawAmt, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : (decimal?)null;
            if (existing.Contains((parsedDate, localAmt)))
                continue;
            transactions.Add(new Transaction
            {
                TranCode = trancode,
                Description = csv.GetField("Description"),
                Security = csv.GetField("Security"),
                TradeDate = parsedDate,
                Quantity = int.TryParse(qfield, out int i) ? i
                    : (decimal.TryParse(qfield, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) ? (int)Math.Round(d) : null),
                Currency = csv.GetField("Local Currency"),
                LocalAmount = localAmt
            });
        }
        scope.AddRange(transactions);
        return await scope.SaveChangesAsync();
    }
    private async Task ImportTicker()
    {
        b1 = true;
        if (string.IsNullOrEmpty(cfg.DownloadsPath))
        {
            await ShowMessage(0);
            return;
        }
        var exfile = Directory.GetFiles(cfg.DownloadsPath, "Ticker*.csv").FirstOrDefault();
        if (exfile is null)
        {
            await ShowMessage(0);
            return;
        }
        using var reader = new StreamReader(exfile);
        using var csv = new CsvReader(reader, CsvConfig());
        csv.Context.RegisterClassMap<TickerMap>();
        var ticks = await csv.GetRecordsAsync<Ticker>().ToListAsync();

        await using var scope = repo.BeginScope();
        var existingSymbols = (await scope.GetEntitiesAsync<Ticker>())
            .Select(t => t.Symbol)
            .ToHashSet();
        var tickers = ticks.Where(t => !existingSymbols.Contains(t.Symbol)).ToList();
        scope.AddRange(tickers);
        int cc = await scope.SaveChangesAsync();
        await ShowMessage(cc);
    }
    private async Task ShowMessage(int cc)
    {
        results.Records = cc;
        results.Success = true;
        StateHasChanged();
        await Task.Delay(5000);
        results = new();
        StateHasChanged();
    }

}
