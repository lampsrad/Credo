using Credo.Models;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;

namespace Credo.Classes;

public class Classes
{
}
public class DecimalNullConverter : DefaultTypeConverter
{
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string cleaned = text.Replace(" ", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
            return result;
        return null;
    }
}
public class IntegerNullConverter : DefaultTypeConverter
{
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string cleaned = text.Replace(" ", "").Trim();
        if (int.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
            return result;
        return null;
    }
}
public class CustomDateConverter : DefaultTypeConverter
{
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return DateOnly.FromDateTime(dt);
        }
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            return DateOnly.FromDateTime(dt);
        }
        return null;
    }
}

public class CurrencyMap : ClassMap<Currency>
{
    private CurrencyMap()
    {
        Map(m => m.Name).Name("currency");
        Map(m => m.Symbol).Name("symbol");
    }
}
public sealed class PortfolioMap : ClassMap<Portfolio>
{
    public PortfolioMap()
    {
        Map(m => m.Quantity).Name("Quantity")
            .Convert(args =>
            {
                string field = args.Row.GetField("Quantity") ?? "";
                field = field.Trim();
                if (string.IsNullOrWhiteSpace(field))
                    return 0;
                if (decimal.TryParse(field, out decimal decimalValue))
                {
                    return (int)Math.Round(decimalValue);
                }
                return 0;
            });
        Map(m => m.Security_Description).Name("Security Description");
        Map(m => m.Unit_Cost).Name("Unit Cost").TypeConverter<DecimalNullConverter>();
        Map(m => m.Price).Name("Price").TypeConverter<DecimalNullConverter>();
        Map(m => m.Cost).Name("Cost").TypeConverter<DecimalNullConverter>();
        Map(m => m.Market_Value).Name("Market Value").TypeConverter<DecimalNullConverter>();
        Map(m => m.Pct)
             .Convert(args =>
             {
                 string field = args.Row.GetField("Pct") ?? "";
                 field = field.Replace("%", "").Replace(" ", "").Trim();
                 if (string.IsNullOrWhiteSpace(field))
                     return 0m;
                 return decimal.TryParse(field, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
             });
        Map(m => m.GainPerc)
             .Convert(args =>
             {
                 string field = args.Row.GetField("P&L %") ?? "";
                 field = field.Replace("%", "").Replace(" ", "").Trim();
                 if (string.IsNullOrWhiteSpace(field))
                     return 0m;
                 return decimal.TryParse(field, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
             });
    }
}
public class TickerMap : ClassMap<Ticker>
{
    private TickerMap()
    {
        Map(m => m.Symbol).Name("ticker");
        Map(m => m.Name).Name("securityname");
        Map(m => m.Currency).Name("currency");
    }
}
public class TransactionMap : ClassMap<Transaction>
{
    public TransactionMap()
    {
        Map(m => m.TranCode).Name("Tran Code").TypeConverter<DecimalNullConverter>();
        Map(m => m.Description).Name("Description").TypeConverter<DecimalNullConverter>();
        Map(m => m.Security).Name("Security").TypeConverter<DecimalNullConverter>();
        Map(m => m.TradeDate).Name("Trade Date").TypeConverter<CustomDateConverter>();
        Map(m => m.Quantity).Name("Quantity").TypeConverter<IntegerNullConverter>();
        Map(m => m.Currency).Name("Local Currency").TypeConverter<DecimalNullConverter>();
        Map(m => m.LocalAmount).Name("Local Amount").TypeConverter<DecimalNullConverter>();
    }
}
