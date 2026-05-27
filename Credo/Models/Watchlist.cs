using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class Watchlist
{
    [Key]
    public int Id { get; set; }
    public string? Symbol { get; set; }
    public string? Name { get; set; }
}
