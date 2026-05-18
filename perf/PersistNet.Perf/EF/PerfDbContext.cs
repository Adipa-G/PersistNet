using Microsoft.EntityFrameworkCore;

namespace PersistNet.Perf.EF;

public sealed class PerfDbContext : DbContext
{
    public DbSet<EfProduct>     Products     => Set<EfProduct>();
    public DbSet<EfOrder>       Orders       => Set<EfOrder>();
    public DbSet<EfOrderItem>   OrderItems   => Set<EfOrderItem>();
    public DbSet<EfOrderCharge> OrderCharges => Set<EfOrderCharge>();

    public PerfDbContext(DbContextOptions<PerfDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EfOrderItem>()
            .HasOne<EfOrder>()
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EfOrderItem>()
            .HasOne<EfProduct>()
            .WithMany()
            .HasForeignKey(i => i.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EfOrderCharge>()
            .HasOne<EfOrderItem>()
            .WithMany(i => i.Charges)
            .HasForeignKey(c => c.ItemLineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
