using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Services;

/// <summary>
/// Background service that updates daily summary metrics
/// </summary>
public class DailySummaryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisSettings _settings;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisSettings> settings,
        ILogger<DailySummaryService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[DailySummary] Starting daily summary service");

        // Initial run after startup
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        await UpdateDailySummaryAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Calculate delay until next update hour
            var now = DateTime.UtcNow;
            var nextRun = now.Date.AddDays(1).AddHours(_settings.DailySummaryUpdateHour);
            if (now.Hour < _settings.DailySummaryUpdateHour)
            {
                nextRun = now.Date.AddHours(_settings.DailySummaryUpdateHour);
            }

            var delay = nextRun - now;
            _logger.LogInformation("[DailySummary] Next update scheduled for {NextRun} (in {Delay})",
                nextRun, delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
                await UpdateDailySummaryAsync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DailySummary] Error updating daily summary");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task UpdateDailySummaryAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlisDbContext>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var yesterday = today.AddDays(-1);

        _logger.LogInformation("[DailySummary] Updating daily summaries");

        // Update summaries for last 7 days
        for (var i = 0; i < 7; i++)
        {
            var date = today.AddDays(-i);
            await UpdateSummaryForDateAsync(db, date, ct);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DailySummary] Daily summaries updated");
    }

    private async Task UpdateSummaryForDateAsync(FlisDbContext db, DateOnly date, CancellationToken ct)
    {
        var dateStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dateEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Get transactions for this date
        var dayTransactions = await db.FlashLoanTransactions
            .Where(tx => tx.Timestamp >= dateStart && tx.Timestamp < dateEnd)
            .ToListAsync(ct);

        // Get features processed for this date
        var featuresProcessed = await db.Features
            .Where(f => f.CreatedAt >= dateStart && f.CreatedAt < dateEnd)
            .CountAsync(ct);

        // Get active patterns count
        var activePatternsCount = await db.Patterns
            .Where(p => p.IsActive)
            .CountAsync(ct);

        // Get top performing pattern
        var topPattern = await db.Patterns
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.SuccessRate * p.AvgProfitUsd)
            .FirstOrDefaultAsync(ct);

        // Get latest model accuracy
        var latestModel = await db.MlModels
            .Where(m => m.IsActive && m.ModelType == "BinaryClassification")
            .OrderByDescending(m => m.TrainedAt)
            .FirstOrDefaultAsync(ct);

        // Calculate metrics
        var totalTxs = dayTransactions.Count;
        var profitableTxs = dayTransactions.Count(tx => tx.IsProfitable);
        var totalProfit = dayTransactions.Sum(tx => tx.ProfitAmount);
        var successRate = totalTxs > 0 ? (decimal)profitableTxs / totalTxs : 0;

        // Find or create summary
        var summary = await db.DailySummaries.FindAsync(new object[] { date }, ct);

        if (summary == null)
        {
            summary = new FlisDailySummary
            {
                SummaryDate = date
            };
            await db.DailySummaries.AddAsync(summary, ct);
        }

        // Update values
        summary.TotalTxsScanned = totalTxs;
        summary.ProfitableTxsIdentified = profitableTxs;
        summary.TotalSimulatedProfitUsd = totalProfit;
        summary.AvgSuccessRate = successRate;
        summary.ActivePatternsCount = activePatternsCount;
        summary.TopPatternId = topPattern?.Id;
        summary.ModelAccuracy = latestModel?.Accuracy;
        summary.FeaturesProcessed = featuresProcessed;

        _logger.LogDebug(
            "[DailySummary] Updated {Date}: {Txs} txs, {Profitable} profitable, ${Profit:F2} total profit",
            date, totalTxs, profitableTxs, totalProfit);
    }
}
