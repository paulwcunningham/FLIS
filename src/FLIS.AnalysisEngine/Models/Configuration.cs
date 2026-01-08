namespace FLIS.AnalysisEngine.Models;

public class AnalysisSettings
{
    public int FeatureEngineeringIntervalMinutes { get; set; } = 5;
    public int PatternRecognitionIntervalHours { get; set; } = 24;
    public int ModelTrainingIntervalHours { get; set; } = 24;
    public int DailySummaryUpdateHour { get; set; } = 0;
    public int MinSamplesForTraining { get; set; } = 100;
    public int MinSamplesForClustering { get; set; } = 50;
    public int ClusterCount { get; set; } = 10;
}

public class ModelSettings
{
    public string ModelsDirectory { get; set; } = "./models";
    public string ProfitabilityClassifierFile { get; set; } = "profitability_classifier.zip";
    public string ProfitPredictorFile { get; set; } = "profit_predictor.zip";
}
