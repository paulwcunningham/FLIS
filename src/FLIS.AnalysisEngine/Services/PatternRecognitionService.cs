using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Services;

/// <summary>
/// Background service that identifies profitable trading patterns using clustering
/// </summary>
public class PatternRecognitionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisSettings _settings;
    private readonly ILogger<PatternRecognitionService> _logger;

    public PatternRecognitionService(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisSettings> settings,
        ILogger<PatternRecognitionService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PatternRecognition] Starting pattern recognition service");

        // Initial delay to allow feature engineering to populate data
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        var interval = TimeSpan.FromHours(_settings.PatternRecognitionIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IdentifyPatternsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PatternRecognition] Error identifying patterns");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task IdentifyPatternsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlisDbContext>();

        // Get profitable features for clustering
        var profitableFeatures = await db.Features
            .Where(f => f.IsProfitable && f.NetProfitUsd > 0)
            .OrderByDescending(f => f.CreatedAt)
            .Take(1000) // Limit for performance
            .ToListAsync(ct);

        if (profitableFeatures.Count < _settings.MinSamplesForClustering)
        {
            _logger.LogInformation(
                "[PatternRecognition] Not enough samples for clustering ({Count}/{Required})",
                profitableFeatures.Count, _settings.MinSamplesForClustering);
            return;
        }

        _logger.LogInformation("[PatternRecognition] Clustering {Count} profitable transactions", profitableFeatures.Count);

        // Perform K-Means clustering
        var clusters = PerformKMeansClustering(profitableFeatures, _settings.ClusterCount);

        // Update cluster assignments in features
        foreach (var feature in profitableFeatures)
        {
            if (clusters.TryGetValue(feature.Id, out var clusterId))
            {
                feature.ClusterId = clusterId;
            }
        }

        // Analyze and create/update patterns for each cluster
        var clusterGroups = profitableFeatures
            .Where(f => f.ClusterId.HasValue)
            .GroupBy(f => f.ClusterId!.Value)
            .ToList();

        foreach (var cluster in clusterGroups)
        {
            var pattern = await CreateOrUpdatePatternAsync(db, cluster.Key, cluster.ToList(), ct);

            // Link features to pattern
            foreach (var feature in cluster)
            {
                feature.PatternId = pattern.Id;
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[PatternRecognition] Identified {Count} patterns", clusterGroups.Count);
    }

    private Dictionary<long, int> PerformKMeansClustering(List<FlisFeature> features, int k)
    {
        // Convert features to vectors for clustering
        var vectors = features.Select(f => new[]
        {
            f.HourOfDay / 24f,           // Normalize hour
            f.DayOfWeek / 7f,            // Normalize day
            f.IsWeekend ? 1f : 0f,
            f.GasPriceGwei / 1000f,      // Normalize gas
            f.LoanAmountUsd / 100000f,   // Normalize loan amount
            f.ActionCount / 10f,          // Normalize action count
            f.NetProfitUsd / 10000f      // Normalize profit
        }).ToList();

        // Initialize centroids randomly
        var random = new Random(42);
        var centroids = Enumerable.Range(0, k)
            .Select(_ => vectors[random.Next(vectors.Count)].ToArray())
            .ToList();

        var assignments = new int[features.Count];
        var maxIterations = 100;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var changed = false;

            // Assign each point to nearest centroid
            for (var i = 0; i < vectors.Count; i++)
            {
                var nearestCentroid = 0;
                var minDistance = double.MaxValue;

                for (var j = 0; j < k; j++)
                {
                    var distance = EuclideanDistance(vectors[i], centroids[j]);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearestCentroid = j;
                    }
                }

                if (assignments[i] != nearestCentroid)
                {
                    assignments[i] = nearestCentroid;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update centroids
            for (var j = 0; j < k; j++)
            {
                var clusterPoints = vectors
                    .Where((_, i) => assignments[i] == j)
                    .ToList();

                if (clusterPoints.Count > 0)
                {
                    var dimensions = centroids[j].Length;
                    for (var d = 0; d < dimensions; d++)
                    {
                        centroids[j][d] = clusterPoints.Average(p => p[d]);
                    }
                }
            }
        }

        // Return mapping of feature ID to cluster ID
        return features
            .Select((f, i) => (f.Id, ClusterId: assignments[i]))
            .ToDictionary(x => x.Id, x => x.ClusterId);
    }

    private double EuclideanDistance(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return Math.Sqrt(sum);
    }

    private async Task<FlisPattern> CreateOrUpdatePatternAsync(
        FlisDbContext db,
        int clusterId,
        List<FlisFeature> clusterFeatures,
        CancellationToken ct)
    {
        // Calculate cluster statistics
        var totalTrades = clusterFeatures.Count;
        var profitableTrades = clusterFeatures.Count(f => f.IsProfitable);
        var successRate = totalTrades > 0 ? (decimal)profitableTrades / totalTrades : 0;
        var avgProfit = clusterFeatures.Average(f => f.NetProfitUsd);
        var totalProfit = clusterFeatures.Sum(f => f.NetProfitUsd);

        // Find typical characteristics
        var typicalHour = (int)Math.Round(clusterFeatures.Average(f => f.HourOfDay));
        var typicalChain = clusterFeatures
            .GroupBy(f => f.ChainId)
            .OrderByDescending(g => g.Count())
            .First().Key;
        var typicalProtocol = clusterFeatures
            .GroupBy(f => f.Protocol)
            .OrderByDescending(g => g.Count())
            .First().Key;
        var typicalTokenPair = clusterFeatures
            .GroupBy(f => f.TokenPair)
            .OrderByDescending(g => g.Count())
            .First().Key;
        var minLoan = (decimal)clusterFeatures.Min(f => f.LoanAmountUsd);
        var maxLoan = (decimal)clusterFeatures.Max(f => f.LoanAmountUsd);
        var typicalGas = (decimal)clusterFeatures.Average(f => f.GasPriceGwei);

        // Check if pattern exists for this cluster
        var existingPattern = await db.Patterns
            .FirstOrDefaultAsync(p => p.TypicalProtocol == typicalProtocol &&
                                      p.TypicalTokenPair == typicalTokenPair &&
                                      p.TypicalChainId == typicalChain, ct);

        if (existingPattern != null)
        {
            // Update existing pattern
            existingPattern.TotalTrades = totalTrades;
            existingPattern.ProfitableTrades = profitableTrades;
            existingPattern.SuccessRate = successRate;
            existingPattern.AvgProfitUsd = (decimal)avgProfit;
            existingPattern.TotalProfitUsd = (decimal)totalProfit;
            existingPattern.UpdatedAt = DateTimeOffset.UtcNow;
            existingPattern.TypicalGasPriceGwei = typicalGas;

            return existingPattern;
        }

        // Create new pattern
        var pattern = new FlisPattern
        {
            Name = $"Pattern-{clusterId}-{typicalProtocol}",
            Description = $"{typicalProtocol} {typicalTokenPair} arbitrage around {typicalHour}:00 UTC",
            TypicalHourStart = Math.Max(0, typicalHour - 2),
            TypicalHourEnd = Math.Min(23, typicalHour + 2),
            TypicalChainId = typicalChain,
            TypicalProtocol = typicalProtocol,
            TypicalTokenPair = typicalTokenPair,
            MinLoanSizeUsd = minLoan,
            MaxLoanSizeUsd = maxLoan,
            TypicalGasPriceGwei = typicalGas,
            TotalTrades = totalTrades,
            ProfitableTrades = profitableTrades,
            SuccessRate = successRate,
            AvgProfitUsd = (decimal)avgProfit,
            TotalProfitUsd = (decimal)totalProfit,
            IsActive = successRate > 0.5m && totalTrades >= 10
        };

        await db.Patterns.AddAsync(pattern, ct);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[PatternRecognition] Created pattern: {Name} (Success Rate: {Rate:P1}, Avg Profit: ${Profit:F2})",
            pattern.Name, pattern.SuccessRate, pattern.AvgProfitUsd);

        return pattern;
    }
}
