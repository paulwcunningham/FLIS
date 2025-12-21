using System.Text.Json;

namespace FLIS.DataCollector.Models;

/// <summary>
/// Represents a flash loan transaction for database storage
/// </summary>
public record FlashLoanTransaction
{
    public string TxHash { get; init; } = string.Empty;
    public long BlockNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ChainId { get; init; }
    public string Protocol { get; init; } = string.Empty;
    public string ContractAddress { get; init; } = string.Empty;
    public List<FlashLoanAction> Actions { get; init; } = new();
    public string ProfitToken { get; init; } = string.Empty;
    public decimal ProfitAmount { get; init; }
    public long GasUsed { get; init; }
    public decimal GasPriceGwei { get; init; }
    public decimal EffectiveGasCostUsd { get; init; }
    public bool IsProfitable { get; init; }

    /// <summary>
    /// Serialize actions to JSON for database storage
    /// </summary>
    public string ActionsJson => JsonSerializer.Serialize(Actions);
}

/// <summary>
/// Represents an individual action within a flash loan transaction
/// </summary>
public record FlashLoanAction
{
    public string ActionType { get; init; } = string.Empty; // "borrow", "swap", "repay", "liquidate"
    public string Token { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string? FromToken { get; init; }
    public string? ToToken { get; init; }
    public string? Protocol { get; init; }
    public int OrderIndex { get; init; }
}

/// <summary>
/// Represents a pending flash loan transaction for processing
/// </summary>
public record PendingTransaction
{
    public string TxHash { get; init; } = string.Empty;
    public long BlockNumber { get; init; }
    public DateTimeOffset BlockTimestamp { get; init; }
    public int ChainId { get; init; }
    public string ChainName { get; init; } = string.Empty;
    public string ToAddress { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public decimal Value { get; init; }
    public long GasUsed { get; init; }
    public decimal GasPrice { get; init; }
}

/// <summary>
/// Event log for flash loan detection
/// </summary>
public record FlashLoanEvent
{
    public string EventName { get; init; } = string.Empty;
    public string ContractAddress { get; init; } = string.Empty;
    public Dictionary<string, object> Parameters { get; init; } = new();
    public int LogIndex { get; init; }
}

/// <summary>
/// XRP DEX arbitrage transaction
/// </summary>
public record XRPArbitrageTransaction
{
    public string TxHash { get; init; } = string.Empty;
    public long LedgerIndex { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Account { get; init; } = string.Empty;
    public List<XRPTradeStep> TradeSteps { get; init; } = new();
    public string StartCurrency { get; init; } = string.Empty;
    public decimal StartAmount { get; init; }
    public decimal EndAmount { get; init; }
    public decimal ProfitAmount { get; init; }
    public decimal TransactionCostXRP { get; init; }
    public bool IsProfitable { get; init; }
}

/// <summary>
/// Individual trade step in XRP arbitrage
/// </summary>
public record XRPTradeStep
{
    public string FromCurrency { get; init; } = string.Empty;
    public string ToCurrency { get; init; } = string.Empty;
    public decimal FromAmount { get; init; }
    public decimal ToAmount { get; init; }
    public decimal ExchangeRate { get; init; }
    public int StepIndex { get; init; }
}
