-- FLIS Hourly Summary Table
-- More granular than daily for real-time monitoring

CREATE TABLE IF NOT EXISTS flis_hourly_summary (
    summary_hour TIMESTAMPTZ PRIMARY KEY,
    total_txs_scanned BIGINT DEFAULT 0,
    profitable_txs_identified BIGINT DEFAULT 0,
    total_simulated_profit_usd NUMERIC DEFAULT 0,
    avg_success_rate NUMERIC DEFAULT 0,
    active_patterns_count INT DEFAULT 0,
    model_accuracy NUMERIC DEFAULT 0,
    features_processed INT DEFAULT 0
);

-- Create index for fast lookups
CREATE INDEX IF NOT EXISTS idx_flis_hourly_summary_hour ON flis_hourly_summary (summary_hour DESC);

-- Populate with current data grouped by hour
INSERT INTO flis_hourly_summary (summary_hour, total_txs_scanned, profitable_txs_identified, total_simulated_profit_usd)
SELECT
    date_trunc('hour', timestamp) as summary_hour,
    COUNT(*) as total_txs_scanned,
    SUM(CASE WHEN is_profitable THEN 1 ELSE 0 END) as profitable_txs_identified,
    SUM(profit_amount) as total_simulated_profit_usd
FROM flash_loan_txs
GROUP BY date_trunc('hour', timestamp)
ON CONFLICT (summary_hour) DO UPDATE SET
    total_txs_scanned = EXCLUDED.total_txs_scanned,
    profitable_txs_identified = EXCLUDED.profitable_txs_identified,
    total_simulated_profit_usd = EXCLUDED.total_simulated_profit_usd;
