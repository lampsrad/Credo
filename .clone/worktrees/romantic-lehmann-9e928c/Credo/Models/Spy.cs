using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Credo.Models;

public class Spy
{
    [Key]
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    [Column(TypeName = "decimal(18,4)")]
    public decimal AdjClose { get; set; }
}
