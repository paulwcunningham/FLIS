namespace FLIS.Executor.Models;

/// <summary>
/// Enhanced flash loan execution result with comprehensive metrics for MLOptimizer training.
/// Published to NATS subject: flashloan.result.{chain}
/// </summary>
public record FlashLoanResult(
    // Core identification
    string OpportunityId,
    string ChainName,
    string Strategy,
    string Asset,
    decimal Amount,
    
    // Execution outcome
    bool Success,
    string? Reason,
    string? TransactionHash,
    
    // Gas metrics
    long? GasUsed,
    string? GasPrice,
    decimal? ActualCostUsd,
    
    // Timing metrics (for latency analysis)
    DateTime Timestamp,
    long? OpportunityReceivedAtNanos,
    long? SimulationStartedAtNanos,
    long? SimulationCompletedAtNanos,
    long? TransactionSubmittedAtNanos,
    long? TransactionConfirmedAtNanos,
    
    // Profit metrics
    decimal? EstimatedProfitUsd,
    decimal? ActualProfitUsd,
    decimal? SlippageBps,
    
    // Market context at execution time
    decimal? SpreadBps,
    decimal? OrderBookImbalance,
    decimal? VolatilityPercent,
    
    // MEV metrics
    string? MevProvider,           // "jito", "suave", or null for standard
    string? BundleId,
    int? BundlePosition,
    decimal? MevTipPaid,
    bool? WasFrontrun,
    bool? WasBackrun,
    
    // Source DEX/Protocol info
    string? SourceProtocol,
    string? TargetProtocol,
    string? LiquidityProvider,
    decimal? PoolLiquidity,
    
    // Block info
    long? BlockNumber,
    string? BlockHash,
    int? TransactionIndex
);

/// <summary>
/// Lightweight result for quick status updates (published more frequently)
/// </summary>
public record FlashLoanStatusUpdate(
    string OpportunityId,
    string Status,  // "received", "simulating", "submitting", "pending", "confirmed", "failed"
    DateTime Timestamp,
    string? Details
);
