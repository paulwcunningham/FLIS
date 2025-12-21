using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;
using FLIS.AnalysisEngine.Services;

namespace FLIS.AnalysisEngine.Api;

public static class FlisApiEndpoints
{
    public static void MapFlisApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/flis");

        // Prediction endpoint
        api.MapPost("/predict", PredictHandler)
            .WithName("Predict")
            .WithOpenApi(op =>
            {
                op.Summary = "Predict flash loan profitability";
                op.Description = "Uses trained ML models to predict whether a potential flash loan will be profitable and estimates the profit amount.";
                return op;
            })
            .Produces<PredictionResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Patterns endpoint
        api.MapGet("/patterns", GetPatternsHandler)
            .WithName("GetPatterns")
            .WithOpenApi(op =>
            {
                op.Summary = "Get identified profitable patterns";
                op.Description = "Returns all active trading patterns identified through clustering analysis.";
                return op;
            })
            .Produces<List<PatternResponse>>(StatusCodes.Status200OK);

        // Daily summary endpoint
        api.MapGet("/summary/{date}", GetDailySummaryHandler)
            .WithName("GetDailySummary")
            .WithOpenApi(op =>
            {
                op.Summary = "Get daily summary metrics";
                op.Description = "Returns aggregated metrics for a specific date.";
                return op;
            })
            .Produces<DailySummaryResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        // Model status endpoint
        api.MapGet("/models", GetModelsHandler)
            .WithName("GetModels")
            .WithOpenApi(op =>
            {
                op.Summary = "Get ML model status";
                op.Description = "Returns information about trained ML models.";
                return op;
            })
            .Produces<List<ModelResponse>>(StatusCodes.Status200OK);

        // Health endpoint
        api.MapGet("/health", HealthHandler)
            .WithName("Health")
            .WithOpenApi(op =>
            {
                op.Summary = "Health check";
                op.Description = "Returns the health status of the Analysis Engine.";
                return op;
            })
            .Produces<HealthResponse>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> PredictHandler(
        [FromBody] PredictionRequest request,
        [FromServices] MlModelService mlModelService,
        [FromServices] FlisDbContext db)
    {
        if (string.IsNullOrEmpty(request.TokenPair))
        {
            return Results.BadRequest(new ErrorResponse { Error = "TokenPair is required" });
        }

        if (!mlModelService.ModelsLoaded)
        {
            mlModelService.LoadModels();
        }

        if (!mlModelService.ModelsLoaded)
        {
            return Results.Json(new ErrorResponse
            {
                Error = "ML models not available. Training in progress."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var input = new FeatureInput
        {
            HourOfDay = request.HourOfDay ?? DateTime.UtcNow.Hour,
            DayOfWeek = request.DayOfWeek ?? (int)DateTime.UtcNow.DayOfWeek,
            IsWeekend = request.DayOfWeek.HasValue
                ? (request.DayOfWeek.Value == 0 || request.DayOfWeek.Value == 6 ? 1f : 0f)
                : (DateTime.UtcNow.DayOfWeek == System.DayOfWeek.Saturday ||
                   DateTime.UtcNow.DayOfWeek == System.DayOfWeek.Sunday ? 1f : 0f),
            ChainId = request.ChainId,
            GasPriceGwei = request.GasPriceGwei,
            GasUsed = request.EstimatedGasUsed,
            GasCostUsd = request.GasPriceGwei * request.EstimatedGasUsed / 1_000_000_000f * 3500f, // Rough ETH price
            LoanAmountUsd = request.LoanAmountUsd,
            ActionCount = request.ActionCount,
            Protocol = request.Protocol ?? "Unknown",
            TokenPair = request.TokenPair,
            PoolType = request.PoolType ?? "Unknown"
        };

        var prediction = mlModelService.Predict(input);

        if (prediction == null)
        {
            return Results.Json(new ErrorResponse
            {
                Error = "Prediction failed"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Try to find matching pattern
        var matchingPattern = await db.Patterns
            .Where(p => p.IsActive &&
                       p.TypicalTokenPair == request.TokenPair &&
                       p.TypicalChainId == request.ChainId)
            .OrderByDescending(p => p.SuccessRate)
            .FirstOrDefaultAsync();

        return Results.Ok(new PredictionResponse
        {
            IsProfitablePrediction = prediction.IsProfitablePrediction,
            ProfitabilityConfidence = Math.Round(prediction.ProfitabilityConfidence, 4),
            PredictedNetProfitUsd = Math.Round(prediction.PredictedNetProfitUsd, 2),
            MatchingPatternId = matchingPattern?.Id,
            MatchingPatternName = matchingPattern?.Name,
            PatternSuccessRate = matchingPattern?.SuccessRate
        });
    }

    private static async Task<IResult> GetPatternsHandler(
        [FromServices] FlisDbContext db,
        [FromQuery] bool activeOnly = true)
    {
        var query = db.Patterns.AsQueryable();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }

        var patterns = await query
            .OrderByDescending(p => p.SuccessRate * p.AvgProfitUsd)
            .Take(50)
            .Select(p => new PatternResponse
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                TypicalChainId = p.TypicalChainId,
                TypicalProtocol = p.TypicalProtocol,
                TypicalTokenPair = p.TypicalTokenPair,
                TypicalHourStart = p.TypicalHourStart,
                TypicalHourEnd = p.TypicalHourEnd,
                MinLoanSizeUsd = p.MinLoanSizeUsd,
                MaxLoanSizeUsd = p.MaxLoanSizeUsd,
                TotalTrades = p.TotalTrades,
                ProfitableTrades = p.ProfitableTrades,
                SuccessRate = p.SuccessRate,
                AvgProfitUsd = p.AvgProfitUsd,
                TotalProfitUsd = p.TotalProfitUsd,
                IsActive = p.IsActive,
                UpdatedAt = p.UpdatedAt
            })
            .ToListAsync();

        return Results.Ok(patterns);
    }

    private static async Task<IResult> GetDailySummaryHandler(
        [FromRoute] string date,
        [FromServices] FlisDbContext db)
    {
        if (!DateOnly.TryParse(date, out var parsedDate))
        {
            return Results.BadRequest(new ErrorResponse { Error = "Invalid date format. Use YYYY-MM-DD." });
        }

        var summary = await db.DailySummaries.FindAsync(parsedDate);

        if (summary == null)
        {
            return Results.NotFound(new ErrorResponse { Error = $"No summary found for {date}" });
        }

        return Results.Ok(new DailySummaryResponse
        {
            SummaryDate = summary.SummaryDate.ToString("yyyy-MM-dd"),
            TotalTxsScanned = summary.TotalTxsScanned,
            ProfitableTxsIdentified = summary.ProfitableTxsIdentified,
            TotalSimulatedProfitUsd = summary.TotalSimulatedProfitUsd,
            AvgSuccessRate = summary.AvgSuccessRate,
            ActivePatternsCount = summary.ActivePatternsCount,
            TopPatternId = summary.TopPatternId,
            ModelAccuracy = summary.ModelAccuracy,
            FeaturesProcessed = summary.FeaturesProcessed
        });
    }

    private static async Task<IResult> GetModelsHandler([FromServices] FlisDbContext db)
    {
        var models = await db.MlModels
            .OrderByDescending(m => m.TrainedAt)
            .Take(10)
            .Select(m => new ModelResponse
            {
                Id = m.Id,
                ModelName = m.ModelName,
                ModelType = m.ModelType,
                Version = m.Version,
                TrainedAt = m.TrainedAt,
                TrainingSamples = m.TrainingSamples,
                Accuracy = m.Accuracy,
                RSquared = m.RSquared,
                IsActive = m.IsActive
            })
            .ToListAsync();

        return Results.Ok(models);
    }

    private static async Task<IResult> HealthHandler(
        [FromServices] FlisDbContext db,
        [FromServices] MlModelService mlModelService)
    {
        var dbConnected = false;
        try
        {
            dbConnected = await db.Database.CanConnectAsync();
        }
        catch { }

        var featuresCount = 0L;
        var patternsCount = 0;
        try
        {
            featuresCount = await db.Features.LongCountAsync();
            patternsCount = await db.Patterns.CountAsync(p => p.IsActive);
        }
        catch { }

        return Results.Ok(new HealthResponse
        {
            Status = dbConnected ? "Healthy" : "Degraded",
            DatabaseConnected = dbConnected,
            ModelsLoaded = mlModelService.ModelsLoaded,
            FeaturesCount = featuresCount,
            ActivePatternsCount = patternsCount,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}

// Request/Response DTOs

public class PredictionRequest
{
    public string TokenPair { get; set; } = string.Empty;
    public int ChainId { get; set; }
    public string? Protocol { get; set; }
    public string? PoolType { get; set; }
    public float LoanAmountUsd { get; set; }
    public float GasPriceGwei { get; set; }
    public float EstimatedGasUsed { get; set; }
    public int ActionCount { get; set; }
    public int? HourOfDay { get; set; }
    public int? DayOfWeek { get; set; }
}

public class PredictionResponse
{
    public bool IsProfitablePrediction { get; set; }
    public double ProfitabilityConfidence { get; set; }
    public double PredictedNetProfitUsd { get; set; }
    public long? MatchingPatternId { get; set; }
    public string? MatchingPatternName { get; set; }
    public decimal? PatternSuccessRate { get; set; }
}

public class PatternResponse
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int TypicalChainId { get; set; }
    public string TypicalProtocol { get; set; } = string.Empty;
    public string TypicalTokenPair { get; set; } = string.Empty;
    public int TypicalHourStart { get; set; }
    public int TypicalHourEnd { get; set; }
    public decimal MinLoanSizeUsd { get; set; }
    public decimal MaxLoanSizeUsd { get; set; }
    public int TotalTrades { get; set; }
    public int ProfitableTrades { get; set; }
    public decimal SuccessRate { get; set; }
    public decimal AvgProfitUsd { get; set; }
    public decimal TotalProfitUsd { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DailySummaryResponse
{
    public string SummaryDate { get; set; } = string.Empty;
    public long TotalTxsScanned { get; set; }
    public long ProfitableTxsIdentified { get; set; }
    public decimal TotalSimulatedProfitUsd { get; set; }
    public decimal AvgSuccessRate { get; set; }
    public int ActivePatternsCount { get; set; }
    public long? TopPatternId { get; set; }
    public decimal? ModelAccuracy { get; set; }
    public long FeaturesProcessed { get; set; }
}

public class ModelResponse
{
    public long Id { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ModelType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset TrainedAt { get; set; }
    public int TrainingSamples { get; set; }
    public decimal? Accuracy { get; set; }
    public decimal? RSquared { get; set; }
    public bool IsActive { get; set; }
}

public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public bool DatabaseConnected { get; set; }
    public bool ModelsLoaded { get; set; }
    public long FeaturesCount { get; set; }
    public int ActivePatternsCount { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
