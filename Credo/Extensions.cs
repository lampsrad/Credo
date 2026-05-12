using System.Globalization;

namespace Credo;

public static class Extensions
{
    public static string toDateString(this string w)
    {
        if (string.IsNullOrEmpty(w))
            return "01-01-01";
        string y, m, d;
        w = w.Replace("/", "-");
        string[] data = w.Split("-");
        y = data[2]; d = data[0]; m = data[1];
        return $"{y}-{m}-{d}";
    }
    public static CultureInfo toCultureInfo(this string? cur)
    {
        cur = cur?.ToLower();
        string info = cur switch
        {
            "gbp" => "en-GB",
            "usd" => "en-US",
            "eur" => "en-DE",
            "cad" => "en-CA",
            "hkd" => "en-JP",
            "dkk" => "en-DK",
            "chf" => "de-CH",
            _ => "en-US"
        };
        return new CultureInfo(info);
    }
    public static string toCurrencyString(this decimal amount, string currency)
    {
        CultureInfo ci = currency.toCultureInfo();
        string am = string.Format(ci, "{0:C2}", amount);
        return am;
    }
    public static string toPercentageString(this decimal amount)
    {
        return $"{amount}%";
    }
}
