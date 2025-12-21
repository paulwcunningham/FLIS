using Microsoft.EntityFrameworkCore;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Data;

public class FlisDbContext : DbContext
{
    public FlisDbContext(DbContextOptions<FlisDbContext> options) : base(options)
    {
    }

    public DbSet<FlashLoanTransaction> FlashLoanTransactions { get; set; }
    public DbSet<FlisFeature> Features { get; set; }
    public DbSet<FlisPattern> Patterns { get; set; }
    public DbSet<FlisDailySummary> DailySummaries { get; set; }
    public DbSet<FlisMlModel> MlModels { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // FlashLoanTransaction
        modelBuilder.Entity<FlashLoanTransaction>(entity =>
        {
            entity.HasKey(e => e.TxHash);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.ChainId);
            entity.HasIndex(e => e.Protocol);
            entity.HasIndex(e => e.IsProfitable);
        });

        // FlisFeature
        modelBuilder.Entity<FlisFeature>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TxHash).IsUnique();
            entity.HasIndex(e => e.IsProfitable);
            entity.HasIndex(e => e.ClusterId);
            entity.HasIndex(e => e.PatternId);
            entity.HasIndex(e => e.CreatedAt);
        });

        // FlisPattern
        modelBuilder.Entity<FlisPattern>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.SuccessRate);
        });

        // FlisDailySummary
        modelBuilder.Entity<FlisDailySummary>(entity =>
        {
            entity.HasKey(e => e.SummaryDate);
        });

        // FlisMlModel
        modelBuilder.Entity<FlisMlModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ModelName, e.IsActive });
        });
    }
}
