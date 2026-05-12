using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class Ticker
{
    [Key]
    public int ID { get; set; } 
    public string? Symbol {  get; set; }
    public string? Name { get; set; }
    public string? Currency { get; set; }
}
