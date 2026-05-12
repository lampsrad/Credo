using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class Currency
{
    [Key]
    public int ID { get; set; }
    public string? Symbol {  get; set; }
    public string? Name { get; set; }
    [Precision(18, 4)]
    public decimal Rate { get; set; }   
}
