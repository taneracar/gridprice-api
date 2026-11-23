using GridPrice.Api.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GridPrice.Api.Data;

/// <summary>A pricing location: a hub, a load zone or a single bus.</summary>
public sealed class PriceNode
{
    public required string Id { get; set; } // e.g. HB_HOUSTON
    public required string MarketCode { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; } // Hub, Zone, Bus
}

/// <summary>
/// One published version of one interval's price. Rows are never updated: a correction from the
/// market is a new row with a higher revision, so any past answer can be reproduced with "as of".
/// </summary>
public sealed class PriceRevision
{
    public long Id { get; set; }
    public required string NodeId { get; set; }
    public MarketRun Run { get; set; }
    public DateTime IntervalStartUtc { get; set; }
    public int IntervalMinutes { get; set; }
    public int Revision { get; set; }
    public DateTime ReceivedAtUtc { get; set; }

    public decimal Lmp { get; set; }
    public decimal Energy { get; set; }
    public decimal Congestion { get; set; }
    public decimal Loss { get; set; }

    public PriceNode? Node { get; set; }

    public bool SameValues(decimal lmp, decimal energy, decimal congestion, decimal loss, int minutes) =>
        Lmp == lmp && Energy == energy && Congestion == congestion && Loss == loss && IntervalMinutes == minutes;
}

public sealed class GridDbContext(DbContextOptions<GridDbContext> options) : DbContext(options)
{
    public DbSet<PriceNode> Nodes => Set<PriceNode>();
    public DbSet<PriceRevision> Prices => Set<PriceRevision>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Every DateTime in this model is UTC. SQLite drops DateTimeKind, so re-stamp it on read.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        b.Entity<PriceNode>(e =>
        {
            e.ToTable("nodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.MarketCode).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.HasIndex(x => x.MarketCode);
        });

        b.Entity<PriceRevision>(e =>
        {
            e.ToTable("price_revisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.NodeId).HasMaxLength(64);
            e.Property(x => x.Run).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.IntervalStartUtc).HasConversion(utc);
            e.Property(x => x.ReceivedAtUtc).HasConversion(utc);
            // $/MWh with 5 decimals, the precision ISOs publish. decimal, never float: these get summed.
            foreach (var p in new[] { nameof(PriceRevision.Lmp), nameof(PriceRevision.Energy), nameof(PriceRevision.Congestion), nameof(PriceRevision.Loss) })
                e.Property(p).HasPrecision(12, 5);

            // One row per (interval, revision). Two writers racing to add the same revision collide here.
            e.HasIndex(x => new { x.NodeId, x.Run, x.IntervalStartUtc, x.Revision }).IsUnique();
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}