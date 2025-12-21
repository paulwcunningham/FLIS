-- FLIS Analysis Engine Tables
-- Migration 003: Create tables for feature engineering, patterns, and ML models

-- Features table for ML training data
CREATE TABLE IF NOT EXISTS flis_features (
    id BIGSERIAL PRIMARY KEY,
    tx_hash TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),

    -- Temporal Features
    hour_of_day INT NOT NULL,
    day_of_week INT NOT NULL,
    is_weekend BOOLEAN NOT NULL DEFAULT FALSE,
    minutes_since_hour_start INT NOT NULL DEFAULT 0,

    -- Market Features
    chain_id INT NOT NULL,
    protocol TEXT NOT NULL,
    token_pair TEXT NOT NULL DEFAULT 'Unknown',

    -- Execution Features
    gas_price_gwei REAL NOT NULL DEFAULT 0,
    gas_used REAL NOT NULL DEFAULT 0,
    gas_cost_usd REAL NOT NULL DEFAULT 0,
    loan_amount_usd REAL NOT NULL DEFAULT 0,
    action_count INT NOT NULL DEFAULT 0,

    -- Pool Features
    pool_type TEXT NOT NULL DEFAULT 'Unknown',

    -- Target Variables
    is_profitable BOOLEAN NOT NULL,
    net_profit_usd REAL NOT NULL DEFAULT 0,
    profit_margin_pct REAL NOT NULL DEFAULT 0,

    -- Clustering
    cluster_id INT,
    pattern_id BIGINT
);

-- Indexes for flis_features
CREATE INDEX IF NOT EXISTS idx_flis_features_tx_hash ON flis_features(tx_hash);
CREATE INDEX IF NOT EXISTS idx_flis_features_is_profitable ON flis_features(is_profitable);
CREATE INDEX IF NOT EXISTS idx_flis_features_cluster_id ON flis_features(cluster_id);
CREATE INDEX IF NOT EXISTS idx_flis_features_pattern_id ON flis_features(pattern_id);
CREATE INDEX IF NOT EXISTS idx_flis_features_created_at ON flis_features(created_at DESC);

-- Patterns table for identified profitable strategies
CREATE TABLE IF NOT EXISTS flis_patterns (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),

    -- Pattern Characteristics
    typical_hour_start INT NOT NULL DEFAULT 0,
    typical_hour_end INT NOT NULL DEFAULT 23,
    typical_chain_id INT NOT NULL DEFAULT 0,
    typical_protocol TEXT NOT NULL DEFAULT '',
    typical_token_pair TEXT NOT NULL DEFAULT '',
    min_loan_size_usd NUMERIC(18,2) NOT NULL DEFAULT 0,
    max_loan_size_usd NUMERIC(18,2) NOT NULL DEFAULT 0,
    typical_gas_price_gwei NUMERIC(18,4) NOT NULL DEFAULT 0,

    -- Performance Metrics
    total_trades INT NOT NULL DEFAULT 0,
    profitable_trades INT NOT NULL DEFAULT 0,
    success_rate NUMERIC(5,4) NOT NULL DEFAULT 0,
    avg_profit_usd NUMERIC(18,2) NOT NULL DEFAULT 0,
    total_profit_usd NUMERIC(18,2) NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

-- Indexes for flis_patterns
CREATE INDEX IF NOT EXISTS idx_flis_patterns_is_active ON flis_patterns(is_active);
CREATE INDEX IF NOT EXISTS idx_flis_patterns_success_rate ON flis_patterns(success_rate DESC);

-- ML Models metadata table
CREATE TABLE IF NOT EXISTS flis_ml_models (
    id BIGSERIAL PRIMARY KEY,
    model_name TEXT NOT NULL,
    model_type TEXT NOT NULL,
    version TEXT NOT NULL,
    file_path TEXT NOT NULL,
    trained_at TIMESTAMPTZ DEFAULT NOW(),
    training_samples INT NOT NULL DEFAULT 0,
    accuracy NUMERIC(5,4),
    r_squared NUMERIC(5,4),
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

-- Indexes for flis_ml_models
CREATE INDEX IF NOT EXISTS idx_flis_ml_models_name_active ON flis_ml_models(model_name, is_active);

-- Add new columns to flis_daily_summary if they don't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'flis_daily_summary'
                   AND column_name = 'active_patterns_count') THEN
        ALTER TABLE flis_daily_summary ADD COLUMN active_patterns_count INT DEFAULT 0;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'flis_daily_summary'
                   AND column_name = 'top_pattern_id') THEN
        ALTER TABLE flis_daily_summary ADD COLUMN top_pattern_id BIGINT;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'flis_daily_summary'
                   AND column_name = 'model_accuracy') THEN
        ALTER TABLE flis_daily_summary ADD COLUMN model_accuracy NUMERIC(5,4);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'flis_daily_summary'
                   AND column_name = 'features_processed') THEN
        ALTER TABLE flis_daily_summary ADD COLUMN features_processed BIGINT DEFAULT 0;
    END IF;
END $$;

-- Grant permissions (adjust as needed)
-- GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO magnus_admin;
-- GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO magnus_admin;
