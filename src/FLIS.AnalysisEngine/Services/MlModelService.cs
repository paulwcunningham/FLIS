using Microsoft.Extensions.Options;
using Microsoft.ML;
using FLIS.AnalysisEngine.Models;

namespace FLIS.AnalysisEngine.Services;

/// <summary>
/// Service for loading and using trained ML models for predictions
/// </summary>
public class MlModelService
{
    private readonly ModelSettings _settings;
    private readonly ILogger<MlModelService> _logger;
    private readonly MLContext _mlContext;

    private ITransformer? _profitabilityClassifier;
    private ITransformer? _profitPredictor;
    private PredictionEngine<FeatureInput, ProfitabilityPrediction>? _profitabilityEngine;
    private PredictionEngine<FeatureInput, ProfitPrediction>? _profitEngine;

    private DateTime _lastModelLoad = DateTime.MinValue;
    private readonly object _loadLock = new();

    public MlModelService(IOptions<ModelSettings> settings, ILogger<MlModelService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _mlContext = new MLContext(seed: 42);
    }

    public bool ModelsLoaded => _profitabilityEngine != null && _profitEngine != null;

    public void LoadModels()
    {
        lock (_loadLock)
        {
            try
            {
                var classifierPath = Path.Combine(_settings.ModelsDirectory, _settings.ProfitabilityClassifierFile);
                var predictorPath = Path.Combine(_settings.ModelsDirectory, _settings.ProfitPredictorFile);

                if (File.Exists(classifierPath))
                {
                    _profitabilityClassifier = _mlContext.Model.Load(classifierPath, out _);
                    _profitabilityEngine = _mlContext.Model.CreatePredictionEngine<FeatureInput, ProfitabilityPrediction>(_profitabilityClassifier);
                    _logger.LogInformation("[MlModelService] Loaded profitability classifier from {Path}", classifierPath);
                }

                if (File.Exists(predictorPath))
                {
                    _profitPredictor = _mlContext.Model.Load(predictorPath, out _);
                    _profitEngine = _mlContext.Model.CreatePredictionEngine<FeatureInput, ProfitPrediction>(_profitPredictor);
                    _logger.LogInformation("[MlModelService] Loaded profit predictor from {Path}", predictorPath);
                }

                _lastModelLoad = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MlModelService] Error loading models");
            }
        }
    }

    public PredictionResult? Predict(FeatureInput input)
    {
        // Auto-reload models if older than 1 hour
        if ((DateTime.UtcNow - _lastModelLoad).TotalHours > 1)
        {
            LoadModels();
        }

        if (_profitabilityEngine == null || _profitEngine == null)
        {
            return null;
        }

        try
        {
            var profitabilityResult = _profitabilityEngine.Predict(input);
            var profitResult = _profitEngine.Predict(input);

            return new PredictionResult
            {
                IsProfitablePrediction = profitabilityResult.PredictedLabel,
                ProfitabilityConfidence = profitabilityResult.Probability,
                PredictedNetProfitUsd = profitResult.PredictedProfit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MlModelService] Error making prediction");
            return null;
        }
    }
}

/// <summary>
/// Input features for ML models
/// </summary>
public class FeatureInput
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
}

/// <summary>
/// Profitability classification result
/// </summary>
public class ProfitabilityPrediction
{
    public bool PredictedLabel { get; set; }
    public float Probability { get; set; }
    public float Score { get; set; }
}

/// <summary>
/// Profit regression result
/// </summary>
public class ProfitPrediction
{
    public float PredictedProfit { get; set; }
}

/// <summary>
/// Combined prediction result
/// </summary>
public class PredictionResult
{
    public bool IsProfitablePrediction { get; set; }
    public float ProfitabilityConfidence { get; set; }
    public float PredictedNetProfitUsd { get; set; }
    public long? MatchingPatternId { get; set; }
}
