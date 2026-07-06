using Microsoft.EntityFrameworkCore;

namespace bzTorrentClient.Engine.Persistence;

public sealed class BzTorrentClientDbContext : DbContext
{
    public BzTorrentClientDbContext(DbContextOptions<BzTorrentClientDbContext> options)
        : base(options)
    {
    }

    public DbSet<TorrentSessionEntity> TorrentSessions => Set<TorrentSessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TorrentSessionEntity>(entity =>
        {
            entity.ToTable("TorrentSessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InfoHash).IsUnique();
            entity.Property(e => e.State).HasConversion<string>();
        });
    }
}
