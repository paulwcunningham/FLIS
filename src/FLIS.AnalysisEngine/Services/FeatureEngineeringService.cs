using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Services;

/// <summary>
/// Background service that extracts features from raw flash loan transactions
/// </summary>
public class FeatureEngineeringService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisSettings _settings;
    private readonly ILogger<FeatureEngineeringService> _logger;

    public FeatureEngineeringService(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisSettings> settings,
        ILogger<FeatureEngineeringService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[FeatureEngineering] Starting feature engineering service");

        // Initial delay to allow other services to start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        var interval = TimeSpan.FromMinutes(_settings.FeatureEngineeringIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNewTransactionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FeatureEngineering] Error processing transactions");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ProcessNewTransactionsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlisDbContext>();

        // Find transactions that don't have features yet
        var processedTxHashes = (await db.Features
            .Select(f => f.TxHash)
            .ToListAsync(ct))
            .ToHashSet();

        var unprocessedTransactions = await db.FlashLoanTransactions
            .Where(tx => !processedTxHashes.Contains(tx.TxHash))
            .OrderBy(tx => tx.Timestamp)
            .Take(100) // Process in batches
            .ToListAsync(ct);

        if (unprocessedTransactions.Count == 0)
        {
            _logger.LogDebug("[FeatureEngineering] No new transactions to process");
            return;
        }

        _logger.LogInformation("[FeatureEngineering] Processing {Count} new transactions", unprocessedTransactions.Count);

        var features = new List<FlisFeature>();

        foreach (var tx in unprocessedTransactions)
        {
            try
            {
                var feature = ExtractFeatures(tx);
                features.Add(feature);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FeatureEngineering] Error extracting features for tx {TxHash}", tx.TxHash);
            }
        }

        if (features.Count > 0)
        {
            await db.Features.AddRangeAsync(features, ct);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[FeatureEngineering] Saved {Count} feature records", features.Count);
        }
    }

    private FlisFeature ExtractFeatures(FlashLoanTransaction tx)
    {
        // Parse actions JSON to get action count and token pair info
        var actionCount = 0;
        var tokenPair = "Unknown";
        var poolType = "Unknown";

        try
        {
            using var doc = JsonDocument.Parse(tx.Actions);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                actionCount = doc.RootElement.GetArrayLength();

                // Try to extract token pair from first swap action
                foreach (var action in doc.RootElement.EnumerateArray())
                {
                    if (action.TryGetProperty("ActionType", out var actionType) &&
                        actionType.GetString() == "swap")
                    {
                        var fromToken = action.TryGetProperty("FromToken", out var ft) ? ft.GetString() : "";
                        var toToken = action.TryGetProperty("ToToken", out var tt) ? tt.GetString() : "";
                        if (!string.IsNullOrEmpty(fromToken) && !string.IsNullOrEmpty(toToken))
                        {
                            tokenPair = $"{fromToken}/{toToken}";
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // Use defaults if parsing fails
        }

        // Determine pool type from protocol
        poolType = DeterminePoolType(tx.Protocol);

        // Estimate loan amount (simplified - would need actual token prices)
        var loanAmountUsd = EstimateLoanAmountUsd(tx);

        return new FlisFeature
        {
            TxHash = tx.TxHash,
            CreatedAt = DateTimeOffset.UtcNow,

            // Temporal features
            HourOfDay = tx.Timestamp.Hour,
            DayOfWeek = (int)tx.Timestamp.DayOfWeek,
            IsWeekend = tx.Timestamp.DayOfWeek == System.DayOfWeek.Saturday ||
                       tx.Timestamp.DayOfWeek == System.DayOfWeek.Sunday,
            MinutesSinceHourStart = tx.Timestamp.Minute,

            // Market features
            ChainId = tx.ChainId,
            Protocol = tx.Protocol,
            TokenPair = tokenPair,

            // Execution features
            GasPriceGwei = (float)tx.GasPriceGwei,
            GasUsed = (float)tx.GasUsed,
            GasCostUsd = (float)tx.EffectiveGasCostUsd,
            LoanAmountUsd = loanAmountUsd,
            ActionCount = actionCount,

            // Pool features
            PoolType = poolType,

            // Target variables
            IsProfitable = tx.IsProfitable,
            NetProfitUsd = (float)tx.ProfitAmount,
            ProfitMarginPct = loanAmountUsd > 0 ? (float)tx.ProfitAmount / loanAmountUsd * 100f : 0f
        };
    }

    private string DeterminePoolType(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            var p when p.Contains("uniswap") => "UniswapV3",
            var p when p.Contains("aave") => "AaveV3",
            var p when p.Contains("balancer") => "Balancer",
            var p when p.Contains("curve") => "Curve",
            var p when p.Contains("sushi") => "SushiSwap",
            var p when p.Contains("xrp") || p.Contains("dex") => "XRP_DEX",
            _ => "Other"
        };
    }

    private float EstimateLoanAmountUsd(FlashLoanTransaction tx)
    {
        // For XRP DEX arbitrage, estimate based on gas cost and profit margin
        // Real implementation would track actual amounts from decoded transaction data
        if (tx.Protocol.Contains("XRP"))
        {
            // XRP transactions typically have lower costs
            return Math.Max((float)tx.ProfitAmount * 10f, 1000f);
        }

        // For EVM chains, estimate based on gas used (more gas = larger transaction)
        var baseEstimate = tx.GasUsed / 1000f * 10f; // Rough heuristic
        return Math.Max(baseEstimate, (float)tx.EffectiveGasCostUsd * 20f);
    }
}
