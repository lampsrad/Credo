using Credo.Classes;
using Credo.Models;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Components;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
            b5 = true;
            if (string.IsNullOrEmpty(cfg.DownloadsPath))
            {
                await ShowMessage(0);
                return;
            }

            IEnumerable<Portfolio> rows;
            var csvFile = Directory.GetFiles(cfg.DownloadsPath, "Portfolio*.csv").FirstOrDefault();
            if (csvFile is not null)
                rows = ReadPortfolioCsvRows(csvFile);
            else
            {
                var xlsxFile = Directory.GetFiles(cfg.DownloadsPath, "Portfolio*.xlsx").FirstOrDefault();
                if (xlsxFile is null)
                {
                    await ShowMessage(0);
                    return;
                }
                rows = MapXlsxRowsToPortfolio(ReadPortfolioXlsxRows(xlsxFile));
            }

            var cc = await UpsertPortfolioAsync(rows);
            await ShowMessage(cc);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportPortfolio failed");
            results.Errors.Add(ex.Message);
            StateHasChanged();
        }
    }

    private List<Portfolio> ReadPortfolioCsvRows(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CsvConfig());
        csv.Context.RegisterClassMap<PortfolioMap>();
        return csv.GetRecords<Portfolio>().ToList();
    }

    private static IEnumerable<Portfolio> MapXlsxRowsToPortfolio(IEnumerable<PortRow> xlsxRows) =>
        xlsxRows.Select(row => new Portfolio
        {
            Security_Description = row.SecurityDescription?.Trim(),
            Quantity             = ParsePortInt(row.Quantity),
            Unit_Cost            = ParsePortDecimal(row.UnitCost),
            Price                = ParsePortDecimal(row.Price),
            Cost                 = ParsePortDecimal(row.Cost),
            Market_Value         = ParsePortDecimal(row.MarketValue),
            Pct                  = ParsePortPct(row.Pct),
            GainPerc             = ParsePortPct(row.GainPerc),
        });

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

    // ── Portfolio xlsx helpers ────────────────────────────────────────────────

    private sealed record PortRow(
        string? Quantity, string? SecurityDescription, string? UnitCost,
        string? Price, string? Cost, string? MarketValue, string? Pct, string? GainPerc);

    private static List<PortRow> ReadPortfolioXlsxRows(string path)
    {
        var rows = new List<PortRow>();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var zip = ZipFile.OpenRead(path);

        // Shared strings — most text in a standard xlsx is stored here (type="s")
        string[] shared = [];
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is not null)
        {
            using var ssStream = ssEntry.Open();
            var ssDoc = XDocument.Load(ssStream);
            shared = [.. ssDoc.Descendants(ns + "si").Select(si =>
                string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))];
        }

        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (sheet is null) return rows;
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);

        // colMap: header text → column letter (built once the header row is found)
        Dictionary<string, string>? colMap = null;

        foreach (var rowEl in doc.Descendants(ns + "row"))
        {
            var cells = new Dictionary<string, string?>();
            foreach (var c in rowEl.Elements(ns + "c"))
            {
                var cref = (string?)c.Attribute("r");
                if (cref is null) continue;
                var col = new string(cref.TakeWhile(char.IsLetter).ToArray());
                var type = (string?)c.Attribute("t");
                string? val = type switch
                {
                    "s" => int.TryParse(c.Element(ns + "v")?.Value, out int idx) && idx < shared.Length
                            ? shared[idx] : null,
                    "inlineStr" => c.Element(ns + "is")?.Element(ns + "t")?.Value,
                    _ => c.Element(ns + "v")?.Value
                };
                cells[col] = val;
            }

            if (cells.Count == 0) continue;

            // Detect header row by Security Description / SecurityDescription
            if (colMap is null)
            {
                if (cells.Values.Any(v =>
                        string.Equals(v?.Trim(), "Security Description", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v?.Trim(), "SecurityDescription", StringComparison.OrdinalIgnoreCase)))
                    colMap = cells
                        .Where(kv => kv.Value is not null)
                        .ToDictionary(kv => kv.Value!.Trim(), kv => kv.Key, StringComparer.OrdinalIgnoreCase);
                continue; // skip preamble rows and the header row itself
            }

            string? Cell(params string[] headers)
            {
                foreach (var header in headers)
                    if (colMap.TryGetValue(header, out var c))
                        return cells.GetValueOrDefault(c);
                return null;
            }

            rows.Add(new PortRow(
                Quantity:            Cell("Quantity"),
                SecurityDescription: Cell("Security Description", "SecurityDescription"),
                UnitCost:            Cell("Unit Cost", "UnitCost"),
                Price:               Cell("Price"),
                Cost:                Cell("Cost"),
                MarketValue:         Cell("Market Value", "MarketValue"),
                Pct:                 Cell("Pct"),
                GainPerc:            Cell("P&L %")));
        }
        return rows;
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
        var cleaned = s.Replace("%", "").Replace(" ", "").Trim();
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
