using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using FLIS.AnalysisEngine.Data;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Services;

/// <summary>
/// Background service that trains ML models for profitability prediction
/// </summary>
public class ModelTrainingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisSettings _analysisSettings;
    private readonly ModelSettings _modelSettings;
    private readonly MlModelService _mlModelService;
    private readonly ILogger<ModelTrainingService> _logger;
    private readonly MLContext _mlContext;

    public ModelTrainingService(
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisSettings> analysisSettings,
        IOptions<ModelSettings> modelSettings,
        MlModelService mlModelService,
        ILogger<ModelTrainingService> logger)
    {
        _scopeFactory = scopeFactory;
        _analysisSettings = analysisSettings.Value;
        _modelSettings = modelSettings.Value;
        _mlModelService = mlModelService;
        _logger = logger;
        _mlContext = new MLContext(seed: 42);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ModelTraining] Starting model training service");

        // Initial delay to allow feature engineering to populate data
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        var interval = TimeSpan.FromHours(_analysisSettings.ModelTrainingIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrainModelsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelTraining] Error training models");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TrainModelsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlisDbContext>();

        // Get all features for training
        var features = await db.Features
            .OrderByDescending(f => f.CreatedAt)
            .Take(10000) // Use recent data
            .ToListAsync(ct);

        if (features.Count < _analysisSettings.MinSamplesForTraining)
        {
            _logger.LogInformation(
                "[ModelTraining] Not enough samples for training ({Count}/{Required})",
                features.Count, _analysisSettings.MinSamplesForTraining);
            return;
        }

        // Check for sufficient positive and negative samples for binary classification
        var positiveCount = features.Count(f => f.IsProfitable);
        var negativeCount = features.Count - positiveCount;

        // Need at least 10 of each class to train effectively
        const int minClassSamples = 10;
        if (positiveCount < minClassSamples || negativeCount < minClassSamples)
        {
            _logger.LogInformation(
                "[ModelTraining] Imbalanced data - need at least {Min} of each class. Positive: {Pos}, Negative: {Neg}",
                minClassSamples, positiveCount, negativeCount);
            return;
        }

        _logger.LogInformation("[ModelTraining] Training models with {Count} samples (Positive: {Pos}, Negative: {Neg})",
            features.Count, positiveCount, negativeCount);

        // Convert to ML.NET training data
        var trainingData = features.Select(f => new TrainingFeature
        {
            HourOfDay = f.HourOfDay,
            DayOfWeek = f.DayOfWeek,
            IsWeekend = f.IsWeekend ? 1f : 0f,
            ChainId = f.ChainId,
            GasPriceGwei = f.GasPriceGwei,
            GasUsed = f.GasUsed,
            GasCostUsd = f.GasCostUsd,
            LoanAmountUsd = f.LoanAmountUsd,
            ActionCount = f.ActionCount,
            Protocol = f.Protocol,
            TokenPair = f.TokenPair,
            PoolType = f.PoolType,
            IsProfitable = f.IsProfitable,
            NetProfitUsd = f.NetProfitUsd
        }).ToList();

        // Train profitability classifier
        var classifierMetrics = await TrainProfitabilityClassifierAsync(trainingData, ct);

        // Train profit predictor
        var predictorMetrics = await TrainProfitPredictorAsync(trainingData, ct);

        // Save model metadata
        await SaveModelMetadataAsync(db, "ProfitabilityClassifier", "BinaryClassification",
            classifierMetrics.accuracy, null, features.Count, ct);

        await SaveModelMetadataAsync(db, "ProfitPredictor", "Regression",
            null, predictorMetrics.rSquared, features.Count, ct);

        // Reload models in the model service
        _mlModelService.LoadModels();

        _logger.LogInformation(
            "[ModelTraining] Models trained successfully. Classifier Accuracy: {Accuracy:P2}, Predictor R²: {RSquared:F4}",
            classifierMetrics.accuracy, predictorMetrics.rSquared);
    }

    private async Task<(double accuracy, double auc)> TrainProfitabilityClassifierAsync(
        List<TrainingFeature> data, CancellationToken ct)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(data);

        // Stratified split to ensure both classes are in train and test sets
        var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2,
            samplingKeyColumnName: "IsProfitable");

        // Build pipeline
        var pipeline = _mlContext.Transforms.Categorical.OneHotEncoding("ProtocolEncoded", "Protocol")
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("TokenPairEncoded", "TokenPair"))
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("PoolTypeEncoded", "PoolType"))
            .Append(_mlContext.Transforms.Concatenate("Features",
                "HourOfDay", "DayOfWeek", "IsWeekend", "ChainId",
                "GasPriceGwei", "GasUsed", "GasCostUsd", "LoanAmountUsd", "ActionCount",
                "ProtocolEncoded", "TokenPairEncoded", "PoolTypeEncoded"))
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(
                labelColumnName: "IsProfitable",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 10));

        // Train
        var model = pipeline.Fit(split.TrainSet);

        // Evaluate with error handling for edge cases
        var predictions = model.Transform(split.TestSet);
        double accuracy = 0;
        double auc = 0;
        double f1 = 0;

        try
        {
            var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "IsProfitable");
            accuracy = metrics.Accuracy;
            auc = double.IsNaN(metrics.AreaUnderRocCurve) ? 0 : metrics.AreaUnderRocCurve;
            f1 = double.IsNaN(metrics.F1Score) ? 0 : metrics.F1Score;
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "PosSample" || ex.ParamName == "NegSample")
        {
            // This happens when test set has no positive or negative samples
            _logger.LogWarning("[ModelTraining] Could not calculate all metrics due to data distribution: {Message}", ex.Message);
            // Use cross-validation metrics instead
            var cvResults = _mlContext.BinaryClassification.CrossValidate(
                _mlContext.Data.LoadFromEnumerable(data),
                pipeline,
                numberOfFolds: 5,
                labelColumnName: "IsProfitable");
            accuracy = cvResults.Average(r => r.Metrics.Accuracy);
            auc = cvResults.Average(r => double.IsNaN(r.Metrics.AreaUnderRocCurve) ? 0 : r.Metrics.AreaUnderRocCurve);
            f1 = cvResults.Average(r => double.IsNaN(r.Metrics.F1Score) ? 0 : r.Metrics.F1Score);
            _logger.LogInformation("[ModelTraining] Used cross-validation metrics instead");
        }

        // Save model
        var modelPath = Path.Combine(_modelSettings.ModelsDirectory, _modelSettings.ProfitabilityClassifierFile);
        _mlContext.Model.Save(model, dataView.Schema, modelPath);

        _logger.LogInformation("[ModelTraining] Profitability classifier saved to {Path}", modelPath);
        _logger.LogInformation("[ModelTraining] Classifier metrics - Accuracy: {Accuracy:P2}, AUC: {AUC:F4}, F1: {F1:F4}",
            accuracy, auc, f1);

        return (accuracy, auc);
    }

    private async Task<(double rSquared, double mae)> TrainProfitPredictorAsync(
        List<TrainingFeature> data, CancellationToken ct)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(data);

        // Split data
        var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

        // Build pipeline
        var pipeline = _mlContext.Transforms.Categorical.OneHotEncoding("ProtocolEncoded", "Protocol")
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("TokenPairEncoded", "TokenPair"))
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("PoolTypeEncoded", "PoolType"))
            .Append(_mlContext.Transforms.Concatenate("Features",
                "HourOfDay", "DayOfWeek", "IsWeekend", "ChainId",
                "GasPriceGwei", "GasUsed", "GasCostUsd", "LoanAmountUsd", "ActionCount",
                "ProtocolEncoded", "TokenPairEncoded", "PoolTypeEncoded"))
            .Append(_mlContext.Regression.Trainers.FastTree(
                labelColumnName: "NetProfitUsd",
                featureColumnName: "Features",
                numberOfLeaves: 20,
                numberOfTrees: 100,
                minimumExampleCountPerLeaf: 10));

        // Train
        var model = pipeline.Fit(split.TrainSet);

        // Evaluate
        var predictions = model.Transform(split.TestSet);
        var metrics = _mlContext.Regression.Evaluate(predictions, labelColumnName: "NetProfitUsd");

        // Save model
        var modelPath = Path.Combine(_modelSettings.ModelsDirectory, _modelSettings.ProfitPredictorFile);
        _mlContext.Model.Save(model, dataView.Schema, modelPath);

        _logger.LogInformation("[ModelTraining] Profit predictor saved to {Path}", modelPath);
        _logger.LogInformation("[ModelTraining] Predictor metrics - R²: {RSquared:F4}, MAE: {MAE:F2}, RMSE: {RMSE:F2}",
            metrics.RSquared, metrics.MeanAbsoluteError, metrics.RootMeanSquaredError);

        return (metrics.RSquared, metrics.MeanAbsoluteError);
    }

    private async Task SaveModelMetadataAsync(
        FlisDbContext db,
        string modelName,
        string modelType,
        double? accuracy,
        double? rSquared,
        int trainingSamples,
        CancellationToken ct)
    {
        // Deactivate previous versions
        var previousModels = await db.MlModels
            .Where(m => m.ModelName == modelName && m.IsActive)
            .ToListAsync(ct);

        foreach (var prev in previousModels)
        {
            prev.IsActive = false;
        }

        // Add new model record
        var modelRecord = new FlisMlModel
        {
            ModelName = modelName,
            ModelType = modelType,
            Version = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            FilePath = modelName == "ProfitabilityClassifier"
                ? Path.Combine(_modelSettings.ModelsDirectory, _modelSettings.ProfitabilityClassifierFile)
                : Path.Combine(_modelSettings.ModelsDirectory, _modelSettings.ProfitPredictorFile),
            TrainedAt = DateTimeOffset.UtcNow,
            TrainingSamples = trainingSamples,
            Accuracy = accuracy.HasValue ? (decimal)accuracy.Value : null,
            RSquared = rSquared.HasValue ? (decimal)rSquared.Value : null,
            IsActive = true
        };

        await db.MlModels.AddAsync(modelRecord, ct);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Training data class for ML.NET
/// </summary>
public class TrainingFeature
{
    public float HourOfDay { get; set; }
    public float DayOfWeek { get; set; }
    public float IsWeekend { get; set; }
    public float ChainId { get; set; }
    public float GasPriceGwei { get; set; }
    public float GasUsed { get; set; }
    public float GasCostUsd { get; set; }
    public float LoanAmountUsd { get; set; }
    public float ActionCount { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string TokenPair { get; set; } = string.Empty;
    public string PoolType { get; set; } = string.Empty;

    [ColumnName("IsProfitable")]
    public bool IsProfitable { get; set; }

    [ColumnName("NetProfitUsd")]
    public float NetProfitUsd { get; set; }
}

