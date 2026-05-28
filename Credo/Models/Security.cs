using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Credo.Models;

public class Security
{
    public Security()
    {
       this.Transactions = new HashSet<Transaction>();
    }
    [Key]
    public int Id { get; set; }
    public int? TickerID { get; set; }
    public virtual Ticker? ticker { get; set; }
    public int? CurrencyID { get; set; }
    public virtual Currency? currency { get; set; }
    public string? SecurityName { get; set; }
    public string? Exchange { get; set; }
    public string? Currency { get; set; }
    [Precision(18,4)]
    public decimal?Price { get; set; }
    public decimal? PrevClose { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? XIRR { get; set; }
    public decimal? Gain { get; set; }
    public decimal? DividendYield { get; set; }
    public decimal? EPS { get; set; }//Earnings / share for the last 12 months; Netto Income (Earnings) / Outstanding Stock (Trailing) in conrast to EpsForward (estimate)
    public decimal? PE { get; set; }// Price / Earnigs ratio
    public decimal? PEf { get; set; }// Forwarded (esteimate)
    public decimal? MarketCap { get; set; }// Sharres outstanding x Current Price
    public virtual ICollection<Transaction> Transactions { get; set; }
    [NotMapped]
    public bool Selected { get; set; }
    [NotMapped]
    public decimal? SpyPerf { get; set; }
    [NotMapped]
    public decimal? GainPerc { get; set; }
}
