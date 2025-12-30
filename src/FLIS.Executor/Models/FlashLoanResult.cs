namespace FLIS.Executor.Models;

/// <summary>
/// Represents the result of a flash loan execution attempt.
/// Published to NATS for MLOptimizer to learn from.
/// </summary>
public class FlashLoanResult
{
    public string OpportunityId { get; set; } = string.Empty;
    public string ChainName { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? TransactionHash { get; set; }
    public long? GasUsed { get; set; }
    public decimal? GasPriceGwei { get; set; }
    public decimal? ActualCostUsd { get; set; }
    public decimal? ActualProfitUsd { get; set; }
    public DateTime Timestamp { get; set; }
}
