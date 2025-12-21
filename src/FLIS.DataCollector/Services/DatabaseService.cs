using Npgsql;
using FLIS.DataCollector.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace FLIS.DataCollector.Services;

/// <summary>
/// Database service for persisting flash loan transactions
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private readonly ConcurrentQueue<FlashLoanTransaction> _batchQueue = new();
    private readonly int _batchSize;

    public DatabaseService(string connectionString, int batchSize, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString;
        _batchSize = batchSize;
        _logger = logger;
    }

    /// <summary>
    /// Queue a transaction for batch insertion
    /// </summary>
    public void QueueTransaction(FlashLoanTransaction transaction)
    {
        _batchQueue.Enqueue(transaction);

        if (_batchQueue.Count >= _batchSize)
        {
            _ = FlushBatchAsync();
        }
    }

    /// <summary>
    /// Flush pending transactions to database
    /// </summary>
    public async Task FlushBatchAsync(CancellationToken ct = default)
    {
        var batch = new List<FlashLoanTransaction>();

        while (_batchQueue.TryDequeue(out var tx) && batch.Count < _batchSize)
        {
            batch.Add(tx);
        }

        if (batch.Count == 0) return;

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            var insertSql = @"
                INSERT INTO flash_loan_txs
                (tx_hash, block_number, timestamp, chain_id, protocol, contract_address,
                 actions, profit_token, profit_amount, gas_used, gas_price_gwei,
                 effective_gas_cost_usd, is_profitable)
                VALUES
                (@tx_hash, @block_number, @timestamp, @chain_id, @protocol, @contract_address,
                 @actions::jsonb, @profit_token, @profit_amount, @gas_used, @gas_price_gwei,
                 @effective_gas_cost_usd, @is_profitable)
                ON CONFLICT (tx_hash) DO NOTHING";

            await using var transaction = await connection.BeginTransactionAsync(ct);

            foreach (var tx in batch)
            {
                await using var cmd = new NpgsqlCommand(insertSql, connection, transaction);
                cmd.Parameters.AddWithValue("tx_hash", tx.TxHash);
                cmd.Parameters.AddWithValue("block_number", tx.BlockNumber);
                cmd.Parameters.AddWithValue("timestamp", tx.Timestamp);
                cmd.Parameters.AddWithValue("chain_id", tx.ChainId);
                cmd.Parameters.AddWithValue("protocol", tx.Protocol);
                cmd.Parameters.AddWithValue("contract_address", tx.ContractAddress);
                cmd.Parameters.AddWithValue("actions", tx.ActionsJson);
                cmd.Parameters.AddWithValue("profit_token", tx.ProfitToken);
                cmd.Parameters.AddWithValue("profit_amount", tx.ProfitAmount);
                cmd.Parameters.AddWithValue("gas_used", tx.GasUsed);
                cmd.Parameters.AddWithValue("gas_price_gwei", tx.GasPriceGwei);
                cmd.Parameters.AddWithValue("effective_gas_cost_usd", tx.EffectiveGasCostUsd);
                cmd.Parameters.AddWithValue("is_profitable", tx.IsProfitable);

                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            _logger.LogInformation("[Database] Inserted {Count} flash loan transactions", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Database] Failed to insert batch of {Count} transactions", batch.Count);

            // Re-queue failed transactions
            foreach (var tx in batch)
            {
                _batchQueue.Enqueue(tx);
            }
        }
    }

    /// <summary>
    /// Update daily summary metrics
    /// </summary>
    public async Task UpdateDailySummaryAsync(DateOnly date, long txsScanned, long profitableTxs,
        decimal totalProfit, decimal avgSuccessRate, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            var sql = @"
                INSERT INTO flis_daily_summary
                (summary_date, total_txs_scanned, profitable_txs_identified,
                 total_simulated_profit_usd, avg_success_rate)
                VALUES (@date, @txs_scanned, @profitable, @profit, @success_rate)
                ON CONFLICT (summary_date) DO UPDATE SET
                    total_txs_scanned = flis_daily_summary.total_txs_scanned + @txs_scanned,
                    profitable_txs_identified = flis_daily_summary.profitable_txs_identified + @profitable,
                    total_simulated_profit_usd = flis_daily_summary.total_simulated_profit_usd + @profit,
                    avg_success_rate = @success_rate";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("date", date);
            cmd.Parameters.AddWithValue("txs_scanned", txsScanned);
            cmd.Parameters.AddWithValue("profitable", profitableTxs);
            cmd.Parameters.AddWithValue("profit", totalProfit);
            cmd.Parameters.AddWithValue("success_rate", avgSuccessRate);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Database] Failed to update daily summary for {Date}", date);
        }
    }

    /// <summary>
    /// Get transaction count
    /// </summary>
    public async Task<long> GetTransactionCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM flash_loan_txs", connection);
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Database] Failed to get transaction count");
            return 0;
        }
    }

    public void Dispose()
    {
        // Flush any remaining transactions
        FlushBatchAsync().GetAwaiter().GetResult();
    }
}
