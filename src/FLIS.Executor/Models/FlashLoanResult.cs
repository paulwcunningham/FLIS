namespace FLIS.Executor.Models;

public record FlashLoanResult(
    string OpportunityId,
    string ChainName,
    string Strategy,
    string Asset,
    decimal Amount,
    bool Success,
    string? Reason,
    string? TransactionHash,
    long? GasUsed,
    string? GasPrice,
    decimal? ActualCostUsd,
    DateTime Timestamp
);
