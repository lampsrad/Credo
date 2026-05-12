using System.ComponentModel.DataAnnotations;

namespace Credo.Models;

public class Transaction
{
    [Key]
    public int ID { get; set; }
    public string? TranCode { get; set; }
    public string? Description { get; set; }
    public int ? SecurityID { get; set; }
    public virtual Security? security {  get; set; }
    public string? Security { get; set; }
    public DateOnly TradeDate { get; set; }
    public int? Quantity { get; set; }
    public string? Currency { get; set; }
    public decimal? LocalAmount { get; set; }
}
