-- init_db_safe.sql (handles existing objects)

-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Convert existing table to hypertable (if not already)
SELECT create_hypertable('flash_loan_txs', 'timestamp', if_not_exists => TRUE);

-- Table for daily summary metrics for the mobile app
CREATE TABLE IF NOT EXISTS flis_daily_summary (
    summary_date DATE PRIMARY KEY,
    total_txs_scanned BIGINT DEFAULT 0,
    profitable_txs_identified BIGINT DEFAULT 0,
    total_simulated_profit_usd NUMERIC DEFAULT 0,
    avg_success_rate NUMERIC DEFAULT 0
);

-- Add indexes for performance (IF NOT EXISTS)
CREATE INDEX IF NOT EXISTS flash_loan_txs_timestamp_idx ON flash_loan_txs (timestamp DESC);
CREATE INDEX IF NOT EXISTS flash_loan_txs_chain_protocol_idx ON flash_loan_txs (chain_id, protocol);
CREATE INDEX IF NOT EXISTS flash_loan_txs_profitable_idx ON flash_loan_txs (is_profitable);
