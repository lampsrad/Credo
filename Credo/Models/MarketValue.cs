using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class MarketValue
{
    [Key]
    public int ID { get; set; }
    public DateOnly Date { get; set; }
    [Precision(18, 2)]
    public decimal Value { get; set; }
}
