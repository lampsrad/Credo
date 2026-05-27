using Credo.Models;
using System.Text.Json;

namespace Credo.Services;

public class Internet
{
    public async Task<string> ResolveTickerViaApi(Security sec)
    {
        if (string.IsNullOrWhiteSpace(sec.ticker.Name)) return null;

        try
        {
            //var originalName = "goog";
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var originalName = sec.ticker.Name.Trim();

            var url = $"https://query2.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(originalName)}&quotesCount=10&newsCount=0&region=US";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array)
                return null;
            var qenum = quotes.EnumerateArray();
            int count = qenum.Count();  
            foreach (var quote in qenum)
            {
                if (!quote.TryGetProperty("symbol", out var symbolElem) ||
                    !quote.TryGetProperty("exchange", out var exchElem) ||
                    !quote.TryGetProperty("shortname", out var nameElem))
                    continue;
                var ticker = symbolElem.GetString() ?? "";
                var exchange = exchElem.GetString()?.ToUpperInvariant() ?? "";
                var name = nameElem.GetString() ?? "";
                if (string.IsNullOrEmpty(ticker)) continue;
                return ticker;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

