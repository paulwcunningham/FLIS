namespace FLIS.Executor.Models;

public record GasBidResult(
    decimal GasPriceGwei,
    long GasLimit,
    decimal EstimatedCostUsd
);
