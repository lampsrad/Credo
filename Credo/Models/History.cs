using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class History
{
    [Key]
    public int ID { get; set; }
    public string? Symbol { get; set; }
    public DateOnly Date { get; set; }
    public decimal? Price { get; set; }
    public decimal? FiftyDayMA { get; set; }
}
