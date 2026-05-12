using Microsoft.EntityFrameworkCore;

namespace Credo.Models;

public class CredoDbContext : DbContext
{
    public CredoDbContext(DbContextOptions<CredoDbContext> options)
            : base(options)
    {
    }

    public virtual DbSet<Transaction> Transactions { get; set; }
    public virtual DbSet<Security> Securities { get; set; }
    public virtual DbSet<Portfolio> Portfolios { get; set; }
    public virtual DbSet<Ticker> Tickers { get; set; }
    public virtual DbSet<Currency> Currencies { get; set; }
    public virtual DbSet<MarketValue> MarketValues { get; set; }
    public virtual DbSet<History> Histories { get; set; }
}
