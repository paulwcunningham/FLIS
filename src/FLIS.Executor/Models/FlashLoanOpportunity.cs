namespace FLIS.Executor.Models;

/// <summary>
/// Enhanced flash loan opportunity with comprehensive context from SignalEngine and AOI.
/// Received from NATS subject: flashloan.opportunity.{chain}
/// </summary>
public record FlashLoanOpportunity(
    // Core identification
    string Id,
    string ChainName,
    string Asset,
    decimal Amount,
    string Strategy,  // "CrossDex", "MultiHop", "Triangular", "JitoMEV", "SuaveMEV"
    
    // DEX routing
    string? SourceDex,
    string? TargetDex,
    string? IntermediateToken,
    int? UniPoolFee,
    bool? UniFirst,
    string? Path,  // For multi-hop: comma-separated addresses
    
    // Profit expectations
    decimal MinProfit,
    decimal ExpectedProfit,
    decimal ConfidenceScore,  // 0.0 to 1.0 from MLOptimizer
    
    // Timing constraints
    int Deadline,
    long CreatedAtNanos,
    long ExpiresAtNanos,
    
    // Market context from AOI
    decimal? AoiScore,
    string? MarketRegime,  // "Normal", "HighVolatility", "LowVolatility", "CrashRisk"
    decimal? SpreadBps,
    decimal? OrderBookImbalance,
    decimal? VolatilityPercent,
    
    // MEV preferences
    bool UseMev,
    string? PreferredMevProvider,  // "jito", "suave", or null for auto-select
    decimal? MaxMevTip,
    int? TargetBundlePosition,
    
    // Risk parameters from MLOptimizer
    decimal? MaxSlippageBps,
    decimal? MaxGasPriceGwei,
    bool? AllowPartialFill,
    
    // Source tracking
    string? SignalId,
    string? StrategyName,
    string? SourceExchange,
    string? TargetExchange
);
