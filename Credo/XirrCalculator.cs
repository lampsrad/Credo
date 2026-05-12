using Credo.Models;

namespace Credo;

public class XirrCalculator
{
    private const double DaysPerYear = 365.0;

    private static class TranCodes
    {
        public const string Buy = "by";
        public const string LongIn = "li";
        public const string LongOut = "lo";
        public const string Sell = "sl";
        public const string Dividend = "dv";
        public const string Interest = "in";
        public const string Convert = "cv";
    }

    /// <summary>
    /// Calculates the XIRR (internal rate of return) for a series of cash flows.
    /// Positive amounts are inflows; negative amounts are outflows.
    /// Returns <see cref="double.NaN"/> if the rate cannot be determined.
    /// </summary>
    public static double CalculateXirr(IReadOnlyList<(DateOnly Date, decimal Amount)> cashFlows)
    {
        if (cashFlows.Count == 0) return 0.0;
        DateOnly referenceDate = cashFlows.Min(cf => cf.Date);
        return NewtonRaphson(0.1, cashFlows, referenceDate);
    }

    /// <summary>
    /// Calculates the hypothetical XIRR had the same buy/sell cash flows been invested in SPY instead.
    /// Returns <see cref="double.NaN"/> if the rate cannot be determined or there is insufficient data.
    /// </summary>
    public static double CalculateSpyXirr(IList<Transaction> transactions, Dictionary<DateOnly, decimal> spyPrices, decimal currencyRate = 1m)
    {
        if (spyPrices.Count == 0) return double.NaN;
        var sorted = transactions
            .Where(t => t.TranCode == TranCodes.Buy || t.TranCode == TranCodes.LongIn ||
                        t.TranCode == TranCodes.Sell || t.TranCode == TranCodes.LongOut)
            .OrderBy(t => t.TradeDate)
            .ToList();
        if (!sorted.Any(t => t.TranCode == TranCodes.Buy || t.TranCode == TranCodes.LongIn))
            return double.NaN;
        var cashFlows = new List<(DateOnly Date, decimal Amount)>();
        double spyShares = 0;
        double secQty = 0;
        foreach (var t in sorted)
        {
            var spyDate = spyPrices.Keys.Where(d => d >= t.TradeDate).OrderBy(d => d).FirstOrDefault();
            if (spyDate == default) continue;
            var spyPrice = (double)spyPrices[spyDate];
            if (spyPrice == 0) continue;
            var qty = (double)(t.Quantity ?? 0);
            if (t.TranCode == TranCodes.Buy || t.TranCode == TranCodes.LongIn)
            {
                var amountUsd = (double)((t.LocalAmount ?? 0) * currencyRate);
                spyShares += amountUsd / spyPrice;
                secQty += qty;
                cashFlows.Add((t.TradeDate, -(t.LocalAmount ?? 0) * currencyRate));
            }
            else if ((t.TranCode == TranCodes.Sell || t.TranCode == TranCodes.LongOut) && secQty > 0 && spyShares > 0)
            {
                var proportion = Math.Min(qty / secQty, 1.0);
                var spySharesSold = spyShares * proportion;
                cashFlows.Add((t.TradeDate, (decimal)(spySharesSold * spyPrice)));
                spyShares -= spySharesSold;
                secQty -= qty;
            }
        }
        if (cashFlows.Count == 0) return double.NaN;
        var latestSpyDate = spyPrices.Keys.Max();
        if (spyShares > 0)
            cashFlows.Add((latestSpyDate, (decimal)(spyShares * (double)spyPrices[latestSpyDate])));
        if (!cashFlows.Any(cf => cf.Amount < 0) || !cashFlows.Any(cf => cf.Amount > 0))
            return double.NaN;
        if (cashFlows.Count == 0) return 0.0;
        DateOnly referenceDate = cashFlows.Min(cf => cf.Date);
        return NewtonRaphson(0.1, cashFlows, referenceDate);
    }

    /// <summary>
    /// Calculates the XIRR for a list of transactions.
    /// Buys and long entries are treated as outflows; sells, dividends, interest, and conversions as inflows.
    /// Unrecognised transaction codes are silently skipped.
    /// Returns <see cref="double.NaN"/> if the rate cannot be determined.
    /// </summary>
    public static double CalculateXirr(IList<Transaction> transactions)
    {
        var cashFlows = new List<(DateOnly Date, decimal Amount)>();
        foreach (var t in transactions)
        {
            switch (t.TranCode)
            {
                case TranCodes.Buy:
                case TranCodes.LongIn:
                    cashFlows.Add((t.TradeDate, -(t.LocalAmount ?? 0)));
                    break;
                case TranCodes.LongOut:
                case TranCodes.Sell:
                case TranCodes.Dividend:
                case TranCodes.Interest:
                case TranCodes.Convert:
                    cashFlows.Add((t.TradeDate, t.LocalAmount ?? 0));
                    break;
                // Other codes (splits, return-of-capital, etc.) are not modelled — add cases here when needed.
            }
        }
        if (cashFlows.Count == 0)
            return 0.0;
        DateOnly referenceDate = cashFlows.Min(cf => cf.Date);
        return NewtonRaphson(0.1, cashFlows, referenceDate);
    }

    private static double NewtonRaphson(double guess, IReadOnlyList<(DateOnly Date, decimal Amount)> cashFlows, DateOnly referenceDate)
    {
        const double tolerance = 1e-8;
        const int maxIterations = 200;
        double rate = guess;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            double npv = Npv(rate, cashFlows, referenceDate);
            double npvDerivative = NpvDerivative(rate, cashFlows, referenceDate);
            if (Math.Abs(npvDerivative) < 1e-10)
                break;
            double newRate = rate - npv / npvDerivative;
            if (Math.Abs(newRate - rate) < tolerance)
                return newRate;
            rate = newRate;
        }
        return BisectionFallback(cashFlows, referenceDate);
    }

    private static double Npv(double rate, IReadOnlyList<(DateOnly Date, decimal Amount)> cashFlows, DateOnly referenceDate)
    {
        double total = 0.0;
        foreach (var cf in cashFlows)
        {
            double t = (cf.Date.DayNumber - referenceDate.DayNumber) / DaysPerYear;
            total += (double)cf.Amount / Math.Pow(1.0 + rate, t);
        }
        return total;
    }

    private static double NpvDerivative(double rate, IReadOnlyList<(DateOnly Date, decimal Amount)> cashFlows, DateOnly referenceDate)
    {
        double total = 0.0;
        foreach (var cf in cashFlows)
        {
            double t = (cf.Date.DayNumber - referenceDate.DayNumber) / DaysPerYear;
            total += -t * (double)cf.Amount / Math.Pow(1.0 + rate, t + 1.0);
        }
        return total;
    }

    private static double BisectionFallback(IReadOnlyList<(DateOnly Date, decimal Amount)> cashFlows, DateOnly referenceDate)
    {
        double low = -0.999;
        double high = 100.0;
        const double tolerance = 1e-8;
        const int maxIterations = 1000;

        double npvLow = Npv(low, cashFlows, referenceDate);
        double npvHigh = Npv(high, cashFlows, referenceDate);
        if (Math.Sign(npvLow) == Math.Sign(npvHigh))
            return 0;

        for (int i = 0; i < maxIterations; i++)
        {
            double mid = (low + high) / 2.0;
            double npv = Npv(mid, cashFlows, referenceDate);
            if (Math.Abs(npv) < tolerance)
                return mid;
            if (npv > 0)
                low = mid;
            else
                high = mid;
        }
        return 0;
    }
}
