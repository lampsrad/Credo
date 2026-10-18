using Credo.Classes;
using Credo.Models;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

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
            int cc = await ImportPortfolioAsync();
            if (cc < 0)
                return;
            await ShowMessage(cc);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportPortfolio failed");
            results.Errors.Add(ex.Message);
            StateHasChanged();
        }
    }
    // Returns the number of rows saved. Returns -1 when the dialog is closed
    // without a file, or when the PDF has no holdings, so the caller does not
    // show "Imported: 0 records" and the existing portfolio is left in place.
    public async Task<int> ImportPortfolioAsync()
    {
        var file = await state.ShowFileUpload("Import portfolio", "Choose a portfolio holdings PDF.", ".pdf");
        if (file is null || file.Length == 0)
            return -1;
        b5 = true;
        await using var stream = new MemoryStream(file);
        var rows = ReadPortfolioPdfRows(stream);
        if (rows.Count == 0)
        {
            results.Errors.Add("No holdings found in the PDF.");
            StateHasChanged();
            return -1;
        }
        return await UpsertPortfolioAsync(rows);
    }
    // Quantity is right-aligned, so a short number such as "73" starts near the
    // security column. Its right edge still ends before SecurityDescription.
    private static List<Portfolio> ReadPortfolioPdfRows(Stream pdf)
    {
        var rows = new List<Portfolio>();
        using var doc = PdfDocument.Open(pdf);
        double? qtyX = null, secX = null, unitX = null, priceX = null, costX = null, mktX = null, pctX = null, plX = null;
        foreach (var page in doc.GetPages())
        {
            foreach (var line in GroupPdfLines(page))
            {
                var securityHeader = line.Words.FirstOrDefault(w => w.Text == "SecurityDescription");
                if (securityHeader is not null)
                {
                    double? LeftOf(string text) => line.Words.FirstOrDefault(w => w.Text == text)?.BoundingBox.Left;
                    var q = LeftOf("Quantity");
                    var unit = LeftOf("UnitCost");
                    var price = LeftOf("Price");
                    var cost = LeftOf("Cost");
                    var market = LeftOf("MarketValue");
                    var pct = LeftOf("Pct");
                    var pl = LeftOf("P&L");
                    if (q is null || unit is null || price is null || cost is null || market is null || pct is null || pl is null)
                        continue;
                    qtyX = q;
                    secX = securityHeader.BoundingBox.Left;
                    unitX = unit;
                    priceX = price;
                    costX = cost;
                    mktX = market;
                    pctX = pct;
                    plX = pl;
                    continue;
                }
                if (secX is null || qtyX is null || unitX is null || priceX is null || costX is null || mktX is null || pctX is null || plX is null)
                    continue;

                var words = line.Words.Where(w => w.BoundingBox.Left >= qtyX.Value - 80).ToList();
                if (words.Count == 0)
                    continue;

                static double Mid(double a, double b) => (a + b) / 2;
                var secUnit = Mid(secX.Value, unitX.Value);
                var unitPrice = Mid(unitX.Value, priceX.Value);
                var priceCost = Mid(priceX.Value, costX.Value);
                var costMkt = Mid(costX.Value, mktX.Value);
                var mktPct = Mid(mktX.Value, pctX.Value);
                var pctPl = Mid(pctX.Value, plX.Value);
                string Bucket(Word w)
                {
                    if (w.BoundingBox.Right < secX.Value - 2) return "q";
                    var x = w.BoundingBox.Left;
                    if (x < secUnit) return "s";
                    if (x < unitPrice) return "u";
                    if (x < priceCost) return "p";
                    if (x < costMkt) return "c";
                    if (x < mktPct) return "m";
                    if (x < pctPl) return "pct";
                    return "pl";
                }
                string Join(string key) => string.Join(" ", words.Where(w => Bucket(w) == key).Select(w => w.Text));
                var security = Join("s").Trim();
                var quantity = Join("q");
                if (string.IsNullOrWhiteSpace(security))
                    continue;
                if (!decimal.TryParse(quantity.Replace(",", "").Replace(" ", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    continue;

                rows.Add(new Portfolio
                {
                    Security_Description = security,
                    Quantity = ParsePortInt(quantity),
                    Unit_Cost = ParsePortDecimal(StripMoney(Join("u"))),
                    Price = ParsePortDecimal(StripMoney(Join("p"))),
                    Cost = ParsePortDecimal(StripMoney(Join("c"))),
                    Market_Value = ParsePortDecimal(StripMoney(Join("m"))),
                    Pct = ParsePortPct(StripMoney(Join("pct"))),
                    GainPerc = ParsePortPct(StripMoney(Join("pl"))),
                });
            }
        }
        return rows;
    }
    private static string StripMoney(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] != '-' && !char.IsDigit(s[i]))
            i++;
        return s[i..];
    }

    private async Task<int> UpsertPortfolioAsync(IEnumerable<Portfolio> rows)
    {
        await using var scope = repo.BeginScope();
        var currencyByName = (await scope.GetEntitiesAsync<Currency>())
            .Where(c => c.Name is not null)
            .ToDictionary(c => c.Name!, c => c.ID);
        var existingPortfs = (await scope.GetEntitiesAsync<Portfolio>())
            .Where(p => p.Security_Description is not null)
            .ToDictionary(p => p.Security_Description!, p => p);
        var portfs = new List<Portfolio>();
        var seenDescriptions = new HashSet<string>();

        foreach (var row in rows)
        {
            var desc = row.Security_Description?.Trim();
            var marketVal = row.Market_Value;

            if (string.IsNullOrWhiteSpace(desc) || (marketVal ?? 0) == 0 || desc == "CONSTELLATION SOFTWARE-WT 40")
                continue;

            seenDescriptions.Add(desc);

            if (existingPortfs.TryGetValue(desc, out var existing))
            {
                existing.Quantity = row.Quantity;
                existing.Market_Value = marketVal;
                continue;
            }

            var port = new Portfolio
            {
                Security_Description = desc,
                Quantity             = row.Quantity,
                Unit_Cost            = row.Unit_Cost,
                Price                = row.Price,
                Cost                 = row.Cost,
                Market_Value         = marketVal,
                Pct                  = row.Pct,
                GainPerc             = row.GainPerc,
            };

            var sec = await repo.GetEntityNTAsync<Security>(s => s.SecurityName == desc);
            port.SecurityID  = sec?.Id;
            port.CurrencyID  = sec?.CurrencyID;
            port.Price       = sec?.Price;
            if (sec == null)
            {
                var curName = MapCurrencyName(desc);
                port.CurrencyID = currencyByName.TryGetValue(curName, out var id) ? id : (int?)null;
                port.Price = 1;
            }
            portfs.Add(port);
        }

        var toDelete = existingPortfs
            .Where(kvp => !seenDescriptions.Contains(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList();
        scope.RemoveRange(toDelete);
        scope.AddRange(portfs);
        return await scope.SaveChangesAsync();
    }

    private static decimal? ParsePortDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Replace(",", "").Replace(" ", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
    private static int ParsePortInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var cleaned = s.Replace(",", "").Replace(" ", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return (int)Math.Round(d);
        return 0;
    }
    private static decimal ParsePortPct(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        var cleaned = s.Replace("%", "").Replace(",", "").Replace(" ", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }
    private async Task ImportSecurities()
    {
        b4 = true;
        await using var scope = repo.BeginScope();

        var ticks = await scope.GetEntitiesAsync<Ticker>();
        var currencies = (await scope.GetEntitiesAsync<Currency>())
            .Where(c => c.Name is not null)
            .ToDictionary(c => c.Name!, c => c.ID);
        var existingTickerIDs = (await scope.GetEntitiesAsync<Security>())
            .Select(s => s.TickerID)
            .ToHashSet();
        var secs = ticks
            .Where(t => !existingTickerIDs.Contains(t.ID))
            .Select(t => new Security
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
        try
        {
            int cc = await ImportTransactionsAsync();
            if (cc < 0)
                return;
            await ShowMessage(cc);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportTransactions failed");
            results.Errors.Add(ex.Message);
            StateHasChanged();
        }
    }
    // Returns the number of rows saved. Returns -1 when the dialog is closed
    // without a file, so the caller does not show "Imported: 0 records".
    public async Task<int> ImportTransactionsAsync()
    {
        var file = await state.ShowFileUpload("Import transactions", "Choose a transaction summary PDF.", ".pdf");
        if (file is null || file.Length == 0)
            return -1;
        b3 = true;
        await using var stream = new MemoryStream(file);
        return await SaveTxRowsAsync(ReadPdfRows(stream));
    }
    private sealed record TxRow(
        string? TranCode, string? Description, string? Security,
        string? TradeDate, string? Quantity, string? Currency, string? LocalAmount, string? Note);
    // Codes and notes share the left margin. A wrapped security token (INC, ADR, SHS)
    // sits about 100pt to the right. The header word "Security" is inset from that
    // column, so a line is a wrap when it starts 60pt or more to the right of "Code".
    private static readonly Regex TxLineRegex = new(
        @"^(?<code>[a-z]{2})\s+(?<body>.+?)\s+(?<trade>\d{2}/\d{2}/\d{2})(?:\s+(?<settle>\d{2}/\d{2}/\d{2}))?(?:\s+(?<qty>[\d,]+(?:\.\d+)?))?\s+(?<ccy>[A-Z]{3})\s+(?<amt>-?[\d,]+\.\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static List<TxRow> ReadPdfRows(Stream pdf)
    {
        var rows = new List<TxRow>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            double codeX = 47;
            foreach (var line in GroupPdfLines(page))
            {
                var codeWord = line.Words.FirstOrDefault(w => w.Text == "Code");
                if (codeWord is not null)
                    codeX = codeWord.BoundingBox.Left;

                var text = string.Join(" ", line.Words.Select(w => w.Text)).Trim();
                if (SkipPdfLine(text))
                    continue;

                var match = TxLineRegex.Match(text);
                if (match.Success)
                {
                    var (description, security) = SplitTxBody(match.Groups["body"].Value);
                    rows.Add(new TxRow(
                        match.Groups["code"].Value,
                        description,
                        security,
                        match.Groups["trade"].Value,
                        match.Groups["qty"].Success ? match.Groups["qty"].Value : null,
                        match.Groups["ccy"].Value,
                        match.Groups["amt"].Value,
                        null));
                    continue;
                }

                if (rows.Count == 0)
                    continue;

                var last = rows[^1];
                if (line.Words[0].BoundingBox.Left >= codeX + 60)
                    rows[^1] = last with { Security = $"{last.Security} {text}".Trim() };
                else
                {
                    var note = string.IsNullOrEmpty(last.Note) ? text : $"{last.Note} {text}";
                    rows[^1] = last with { Note = note };
                }
            }
        }
        return rows;
    }
    private static List<PdfLine> GroupPdfLines(Page page)
    {
        var words = page.GetWords()
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();
        var lines = new List<PdfLine>();
        foreach (var word in words)
        {
            if (lines.Count == 0 || Math.Abs(lines[^1].Y - word.BoundingBox.Bottom) > 3)
                lines.Add(new PdfLine(word.BoundingBox.Bottom, [word]));
            else
                lines[^1].Words.Add(word);
        }
        foreach (var line in lines)
            line.Words.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        return lines;
    }
    private static bool SkipPdfLine(string text)
    {
        if (text.Length == 0 || text.All(char.IsDigit)) return true;
        if (text.Contains("Sanlam", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Lamprecht", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Transaction Summary", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("From ", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Description", StringComparison.Ordinal) && text.Contains("Security", StringComparison.Ordinal)) return true;
        if (text.StartsWith("Tran ", StringComparison.Ordinal)) return true;
        return false;
    }
    private static (string Description, string Security) SplitTxBody(string body)
    {
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var split = 0;
        for (; split < tokens.Length; split++)
        {
            var letters = tokens[split].Where(char.IsLetter).ToArray();
            if (letters.Length > 0 && letters.All(char.IsUpper))
                break;
        }
        return (string.Join(' ', tokens.Take(split)), string.Join(' ', tokens.Skip(split)));
    }
    private sealed class PdfLine(double y, List<Word> words)
    {
        public double Y { get; } = y;
        public List<Word> Words { get; } = words;
    }
    private async Task<int> SaveTxRowsAsync(IReadOnlyList<TxRow> rows)
    {
        await using var scope = repo.BeginScope();
        var existing = (await scope.GetEntitiesAsync<Transaction>())
            .Select(t => (t.TradeDate, t.LocalAmount))
            .ToHashSet();
        var transactions = new List<Transaction>();
        foreach (var row in rows)
        {
            var trancode = row.TranCode?.Trim();
            if (string.IsNullOrEmpty(trancode)) continue;
            if (string.IsNullOrWhiteSpace(row.TradeDate)) continue;
            var tradedate = DateRegex(DateRegex(row.TradeDate));
            if (!DateOnly.TryParse(tradedate, out var parsedDate)) continue;
            // Strip spaces used as thousands separators and trailing periods before parsing.
            var qfield = row.Quantity?.Replace(" ", "").Trim().TrimEnd('.');
            var rawAmt = row.LocalAmount?.Replace(" ", "");
            var localAmt = decimal.TryParse(rawAmt, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : (decimal?)null;
            if (existing.Contains((parsedDate, localAmt)))
                continue;
            transactions.Add(new Transaction
            {
                TranCode = trancode,
                Description = row.Description,
                Security = row.Security,
                TradeDate = parsedDate,
                Quantity = int.TryParse(qfield, out int i) ? i
                    : (decimal.TryParse(qfield, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) ? (int)Math.Round(d) : null),
                Currency = row.Currency,
                LocalAmount = localAmt,
                Note = row.Note
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
    private async Task LinkTransactions()
    {
        b6 = true;
        await using var scope = repo.BeginScope();

        // Build name → ID map; duplicate names resolve to Milan exchange when present
        var byName = (await scope.GetEntitiesAsync<Security>())
            .Where(s => s.SecurityName is not null)
            .GroupBy(s => s.SecurityName!)
            .ToList();
        var secByName = new Dictionary<string, int>();
        foreach (var g in byName)
        {
            var id = ResolveSecurityId(g);
            if (id is null)
                results.Warnings.Add($"Ambiguous Security name '{g.Key}' ({g.Count()} rows) — transactions not linked.");
            else
                secByName[g.Key] = id.Value;
        }

        var duplicateIds = byName
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(s => s.Id))
            .ToHashSet();

        // Link orphans and correct transactions tied to the wrong duplicate (e.g. NYSE vs Milan)
        var toLink = (await scope.GetEntitiesAsync<Transaction>(t => t.Security != null))
            .Where(t => secByName.ContainsKey(t.Security!)
                && (t.SecurityID == null
                    || (duplicateIds.Contains(t.SecurityID.Value) && t.SecurityID != secByName[t.Security!])))
            .ToList();

        foreach (var t in toLink)
            t.SecurityID = secByName[t.Security!];

        int cc = await scope.SaveChangesAsync();
        await ShowMessage(cc);
    }
    private static int? ResolveSecurityId(IEnumerable<Security> group)
    {
        var list = group.ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].Id;
        return list.FirstOrDefault(s => s.Exchange == "Milan")?.Id;
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
