namespace FLIS.Executor.Models;

public record FlashLoanOpportunity(
    string Id,
    string ChainName,
    string Asset,
    decimal Amount,
    string Strategy, // "CrossDex" or "MultiHop"
    string? SourceDex,
    string? TargetDex,
    string? IntermediateToken,
    int? UniPoolFee,
    bool? UniFirst,
    string? Path, // For multi-hop
    decimal MinProfit,
    int Deadline
);
