using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FLIS.AnalysisEngine.Models;

/// <summary>
/// Flash loan transaction from the data collector
/// </summary>
[Table("flash_loan_txs")]
public class FlashLoanTransaction
{
    [Key]
    [Column("tx_hash")]
    public string TxHash { get; set; } = string.Empty;

    [Column("block_number")]
    public long BlockNumber { get; set; }

    [Column("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [Column("chain_id")]
    public int ChainId { get; set; }

    [Column("protocol")]
    public string Protocol { get; set; } = string.Empty;

    [Column("contract_address")]
    public string ContractAddress { get; set; } = string.Empty;

    [Column("actions", TypeName = "jsonb")]
    public string Actions { get; set; } = "[]";

    [Column("profit_token")]
    public string ProfitToken { get; set; } = string.Empty;

    [Column("profit_amount")]
    public decimal ProfitAmount { get; set; }

    [Column("gas_used")]
    public long GasUsed { get; set; }

    [Column("gas_price_gwei")]
    public decimal GasPriceGwei { get; set; }

    [Column("effective_gas_cost_usd")]
    public decimal EffectiveGasCostUsd { get; set; }

    [Column("is_profitable")]
    public bool IsProfitable { get; set; }
}

/// <summary>
/// Engineered features for ML training
/// </summary>
[Table("flis_features")]
public class FlisFeature
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("tx_hash")]
    public string TxHash { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Temporal Features
    [Column("hour_of_day")]
    public int HourOfDay { get; set; }

    [Column("day_of_week")]
    public int DayOfWeek { get; set; }

    [Column("is_weekend")]
    public bool IsWeekend { get; set; }

    [Column("minutes_since_hour_start")]
    public int MinutesSinceHourStart { get; set; }

    // Market Features
    [Column("chain_id")]
    public int ChainId { get; set; }

    [Column("protocol")]
    public string Protocol { get; set; } = string.Empty;

    [Column("token_pair")]
    public string TokenPair { get; set; } = string.Empty;

    // Execution Features
    [Column("gas_price_gwei")]
    public float GasPriceGwei { get; set; }

    [Column("gas_used")]
    public float GasUsed { get; set; }

    [Column("gas_cost_usd")]
    public float GasCostUsd { get; set; }

    [Column("loan_amount_usd")]
    public float LoanAmountUsd { get; set; }

    [Column("action_count")]
    public int ActionCount { get; set; }

    // Pool Features
    [Column("pool_type")]
    public string PoolType { get; set; } = string.Empty;

    // Target Variables
    [Column("is_profitable")]
    public bool IsProfitable { get; set; }

    [Column("net_profit_usd")]
    public float NetProfitUsd { get; set; }

    [Column("profit_margin_pct")]
    public float ProfitMarginPct { get; set; }

    // Clustering
    [Column("cluster_id")]
    public int? ClusterId { get; set; }

    [Column("pattern_id")]
    public long? PatternId { get; set; }
}

/// <summary>
/// Identified profitable patterns/strategies
/// </summary>
[Table("flis_patterns")]
public class FlisPattern
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string Description { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Pattern Characteristics
    [Column("typical_hour_start")]
    public int TypicalHourStart { get; set; }

    [Column("typical_hour_end")]
    public int TypicalHourEnd { get; set; }

    [Column("typical_chain_id")]
    public int TypicalChainId { get; set; }

    [Column("typical_protocol")]
    public string TypicalProtocol { get; set; } = string.Empty;

    [Column("typical_token_pair")]
    public string TypicalTokenPair { get; set; } = string.Empty;

    [Column("min_loan_size_usd")]
    public decimal MinLoanSizeUsd { get; set; }

    [Column("max_loan_size_usd")]
    public decimal MaxLoanSizeUsd { get; set; }

    [Column("typical_gas_price_gwei")]
    public decimal TypicalGasPriceGwei { get; set; }

    // Performance Metrics
    [Column("total_trades")]
    public int TotalTrades { get; set; }

    [Column("profitable_trades")]
    public int ProfitableTrades { get; set; }

    [Column("success_rate")]
    public decimal SuccessRate { get; set; }

    [Column("avg_profit_usd")]
    public decimal AvgProfitUsd { get; set; }

    [Column("total_profit_usd")]
    public decimal TotalProfitUsd { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Daily summary for dashboard/mobile app
/// </summary>
[Table("flis_daily_summary")]
public class FlisDailySummary
{
    [Key]
    [Column("summary_date")]
    public DateOnly SummaryDate { get; set; }

    [Column("total_txs_scanned")]
    public long TotalTxsScanned { get; set; }

    [Column("profitable_txs_identified")]
    public long ProfitableTxsIdentified { get; set; }

    [Column("total_simulated_profit_usd")]
    public decimal TotalSimulatedProfitUsd { get; set; }

    [Column("avg_success_rate")]
    public decimal AvgSuccessRate { get; set; }

    // Additional metrics
    [Column("active_patterns_count")]
    public int ActivePatternsCount { get; set; }

    [Column("top_pattern_id")]
    public long? TopPatternId { get; set; }

    [Column("model_accuracy")]
    public decimal? ModelAccuracy { get; set; }

    [Column("features_processed")]
    public long FeaturesProcessed { get; set; }
}

/// <summary>
/// ML Model metadata tracking
/// </summary>
[Table("flis_ml_models")]
public class FlisMlModel
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("model_name")]
    public string ModelName { get; set; } = string.Empty;

    [Column("model_type")]
    public string ModelType { get; set; } = string.Empty;

    [Column("version")]
    public string Version { get; set; } = string.Empty;

    [Column("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [Column("trained_at")]
    public DateTimeOffset TrainedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("training_samples")]
    public int TrainingSamples { get; set; }

    [Column("accuracy")]
    public decimal? Accuracy { get; set; }

    [Column("r_squared")]
    public decimal? RSquared { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
