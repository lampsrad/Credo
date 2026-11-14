using Credo.Models;
using CsvHelper;
using System.Globalization;

namespace Credo.Classes;

public class Classes
{
}

public sealed class DecimalNullConverter : ICsvConverter<decimal>
{
    public decimal Read(CsvReader reader, int index)
    {
        var text = reader.GetString(index);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        var cleaned = text.Replace(" ", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : default;
    }

    public void Write(CsvWriter writer, decimal value) =>
        writer.WriteField(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class IntegerNullConverter : ICsvConverter<int>
{
    public int Read(CsvReader reader, int index)
    {
        var text = reader.GetString(index);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        var cleaned = text.Replace(" ", "").Trim();
        return int.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : default;
    }

    public void Write(CsvWriter writer, int value) =>
        writer.WriteField(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class CustomDateConverter : ICsvConverter<DateOnly>
{
    public DateOnly Read(CsvReader reader, int index)
    {
        var text = reader.GetString(index).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return default;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);
        return default;
    }

    public void Write(CsvWriter writer, DateOnly value) =>
        writer.WriteField(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

public sealed class PortfolioQuantityConverter : ICsvConverter<int>
{
    public int Read(CsvReader reader, int index)
    {
        var field = reader.GetString(index).Replace(" ", "").Replace(",", "").Trim();
        if (string.IsNullOrWhiteSpace(field))
            return 0;
        if (decimal.TryParse(field, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return (int)Math.Round(value);
        return 0;
    }

    public void Write(CsvWriter writer, int value) =>
        writer.WriteField(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class PercentConverter : ICsvConverter<decimal>
{
    public decimal Read(CsvReader reader, int index)
    {
        var field = reader.GetString(index).Replace("%", "").Replace(" ", "").Trim();
        if (string.IsNullOrWhiteSpace(field))
            return 0m;
        return decimal.TryParse(field, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    public void Write(CsvWriter writer, decimal value) =>
        writer.WriteField(value.ToString(CultureInfo.InvariantCulture));
}

[CsvRecordMap(typeof(Currency))]
public partial class CurrencyMap
{
    [CsvIgnore]
    public int ID { get; set; }

    [CsvColumn("symbol", Optional = true)]
    public string? Symbol { get; set; }

    [CsvColumn("currency", Optional = true)]
    public string? Name { get; set; }

    [CsvIgnore]
    public decimal Rate { get; set; }
}

[CsvRecordMap(typeof(Portfolio))]
public sealed partial class PortfolioMap
{
    [CsvIgnore]
    public int Id { get; set; }

    [CsvColumn("Quantity", Optional = true)]
    [CsvConverter(typeof(PortfolioQuantityConverter))]
    [CsvDefault(0)]
    public int? Quantity { get; set; }

    [CsvIgnore]
    public int? CurrencyID { get; set; }

    [CsvIgnore]
    public Currency? currency { get; set; }

    [CsvIgnore]
    public int? SecurityID { get; set; }

    [CsvIgnore]
    public Security? security { get; set; }

    [CsvColumn("Security Description", "SecurityDescription", Optional = true)]
    public string? Security_Description { get; set; }

    [CsvColumn("Unit Cost", "UnitCost", Optional = true)]
    [CsvConverter(typeof(DecimalNullConverter))]
    public decimal? Unit_Cost { get; set; }

    [CsvColumn("Price", Optional = true)]
    [CsvConverter(typeof(DecimalNullConverter))]
    public decimal? Price { get; set; }

    [CsvColumn("Cost", Optional = true)]
    [CsvConverter(typeof(DecimalNullConverter))]
    public decimal? Cost { get; set; }

    [CsvIgnore]
    public decimal? Change { get; set; }

    [CsvColumn("Market Value", "MarketValue", Optional = true)]
    [CsvConverter(typeof(DecimalNullConverter))]
    public decimal? Market_Value { get; set; }

    [CsvIgnore]
    public decimal? Gain { get; set; }

    [CsvColumn("P&L %", Optional = true)]
    [CsvConverter(typeof(PercentConverter))]
    [CsvDefault(0)]
    public decimal? GainPerc { get; set; }

    [CsvColumn("Pct", Optional = true)]
    [CsvConverter(typeof(PercentConverter))]
    [CsvDefault(0)]
    public decimal? Pct { get; set; }

    [CsvIgnore]
    public decimal? IRR { get; set; }

    [CsvIgnore]
    public bool Persist { get; set; }

    [CsvIgnore]
    public bool Sold { get; set; }

    [CsvIgnore]
    public decimal Dividend { get; set; }

    [CsvIgnore]
    public bool Updated { get; set; }
}

[CsvRecordMap(typeof(Ticker))]
public partial class TickerMap
{
    [CsvIgnore]
    public int ID { get; set; }

    [CsvColumn("ticker", Optional = true)]
    public string? Symbol { get; set; }

    [CsvColumn("securityname", Optional = true)]
    public string? Name { get; set; }

    [CsvColumn("currency", Optional = true)]
    public string? Currency { get; set; }
}

[CsvRecordMap(typeof(Transaction))]
public partial class TransactionMap
{
    [CsvIgnore]
    public int ID { get; set; }

    [CsvColumn("Tran Code", Optional = true)]
    public string? TranCode { get; set; }

    [CsvColumn("Description", Optional = true)]
    public string? Description { get; set; }

    [CsvIgnore]
    public int? SecurityID { get; set; }

    [CsvIgnore]
    public Security? security { get; set; }

    [CsvColumn("Trade Date", Optional = true)]
    [CsvConverter(typeof(CustomDateConverter))]
    public DateOnly TradeDate { get; set; }

    [CsvColumn("Quantity", Optional = true)]
    [CsvConverter(typeof(IntegerNullConverter))]
    public int? Quantity { get; set; }

    [CsvColumn("Local Currency", Optional = true)]
    public string? Currency { get; set; }

    [CsvColumn("Local Amount", Optional = true)]
    [CsvConverter(typeof(DecimalNullConverter))]
    public decimal? LocalAmount { get; set; }

    [CsvIgnore]
    public string? Note { get; set; }
}
