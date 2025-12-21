using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FLIS.DataCollector.Services;

/// <summary>
/// Health monitoring service that periodically reports status and metrics
/// </summary>
public sealed class HealthMonitorService : BackgroundService
{
    private readonly DatabaseService _database;
    private readonly ILogger<HealthMonitorService> _logger;
    private readonly TimeSpan _reportInterval = TimeSpan.FromMinutes(1);

    private long _lastReportedCount;
    private DateTimeOffset _startTime;

    public HealthMonitorService(
        DatabaseService database,
        ILogger<HealthMonitorService> logger)
    {
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startTime = DateTimeOffset.UtcNow;
        _logger.LogInformation("[HealthMonitor] Starting health monitoring service");

        // Initial delay
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReportHealthAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HealthMonitor] Error reporting health");
            }

            await Task.Delay(_reportInterval, stoppingToken);
        }
    }

    private async Task ReportHealthAsync(CancellationToken ct)
    {
        var currentCount = await _database.GetTransactionCountAsync(ct);
        var newTransactions = currentCount - _lastReportedCount;
        var uptime = DateTimeOffset.UtcNow - _startTime;

        _logger.LogInformation(
            "[HealthMonitor] Status Report | Uptime: {Uptime:hh\\:mm\\:ss} | " +
            "Total Transactions: {Total} | New (last minute): {New}",
            uptime, currentCount, newTransactions);

        _lastReportedCount = currentCount;

        // Flush any pending transactions
        await _database.FlushBatchAsync(ct);
    }
}
