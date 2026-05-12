using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Credo.Models;

public class Portfolio
{
    public int Id { get; set; }
    public int? Quantity { get; set; }
    public int? CurrencyID { get; set; }
    public virtual Currency? currency { get; set; }
    public string? Currency { get; set; }
    public int? SecurityID { get; set; }
    public virtual Security? security { get; set; }
    public string? Security_Description { get; set; }
    [Precision(18, 4)]
    public decimal? Unit_Cost { get; set; }
    public decimal? Cost { get; set; }
    [Precision(18, 4)]
    public decimal? Price { get; set; }
    public decimal? Change { get; set; }
    public decimal? Market_Value { get; set; }
    public decimal? Gain { get; set; }
    public decimal? GainPerc { get; set; }
    public decimal? Pct { get; set; }
    public decimal? IRR { get; set; }
    public bool Persist { get; set; }
    public bool Sold { get; set; }
    [NotMapped]
    public decimal? Market_Value_Yesterday
    {
        get
        {
            if (Quantity is null) return null;
            if (Currency == "USD=X")
            {
                var prev = security?.PrevClose ?? Price;
                return prev * Quantity;
            }
            var prevFx = security?.PrevClose;
            if (prevFx is null) return null;
            return prevFx * Quantity * currency?.Rate;
        }
    }
    [NotMapped]
    public decimal Dividend { get; set; }
    [NotMapped]
    public bool Updated { get; set; }
}
