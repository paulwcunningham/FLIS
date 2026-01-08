-- init_db.sql

-- Enable TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Main table for raw flash loan transactions
CREATE TABLE flash_loan_txs (
    tx_hash TEXT PRIMARY KEY,
    block_number BIGINT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL,
    chain_id INT NOT NULL,
    protocol TEXT NOT NULL,
    contract_address TEXT NOT NULL,
    actions JSONB NOT NULL,
    profit_token TEXT NOT NULL,
    profit_amount NUMERIC NOT NULL,
    gas_used BIGINT NOT NULL,
    gas_price_gwei NUMERIC NOT NULL,
    effective_gas_cost_usd NUMERIC NOT NULL,
    is_profitable BOOLEAN NOT NULL
);

-- Convert the main table to a TimescaleDB hypertable
SELECT create_hypertable('flash_loan_txs', 'timestamp');

-- Table for daily summary metrics for the mobile app
CREATE TABLE flis_daily_summary (
    summary_date DATE PRIMARY KEY,
    total_txs_scanned BIGINT DEFAULT 0,
    profitable_txs_identified BIGINT DEFAULT 0,
    total_simulated_profit_usd NUMERIC DEFAULT 0,
    avg_success_rate NUMERIC DEFAULT 0
);

-- Add indexes for performance
CREATE INDEX ON flash_loan_txs (timestamp DESC);
CREATE INDEX ON flash_loan_txs (chain_id, protocol);
CREATE INDEX ON flash_loan_txs (is_profitable);
