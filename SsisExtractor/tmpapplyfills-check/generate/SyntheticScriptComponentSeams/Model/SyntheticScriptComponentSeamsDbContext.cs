using Microsoft.EntityFrameworkCore;

namespace SyntheticScriptComponentSeams.Model;

public sealed class SyntheticScriptComponentSeamsDbContext(DbContextOptions<SyntheticScriptComponentSeamsDbContext> options) : DbContext(options)
{
    public DbSet<SyntheticScriptSeamsTarget> SyntheticScriptSeamsTargets => Set<SyntheticScriptSeamsTarget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyntheticScriptSeamsTarget>(entity =>
        {
            entity.ToTable("SyntheticScriptSeamsTarget", "dbo");
            entity.HasKey(e => e.ID);
            entity.Property(e => e.ID).ValueGeneratedNever();
            entity.Property(e => e.FirstName).HasMaxLength(50);
            entity.Property(e => e.LastName).HasMaxLength(50);
            entity.Property(e => e.FullName).HasMaxLength(100);
        });
    }
}
