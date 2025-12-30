# Changelog

All notable changes to FLIS (Flash Loan Intelligence Scanner) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-12-30

### Added
- Initial implementation of FLIS.Executor service
- Multi-chain RPC provider supporting Ethereum, Base, and Arbitrum
- NATS integration for opportunity subscription (`magnus.opportunities.flashloan`)
- NATS result publishing (`magnus.results.flashloan`) for ML feedback loop
- ML-powered gas bidding integration with MLOptimizer service
- Transaction simulation using eth_call for profitability verification
- Smart contract interaction for flash loan execution:
  - `executeCrossDexArbitrage` for cross-DEX arbitrage opportunities
  - `executeMultiHopArbitrage` for multi-hop trading paths
- Comprehensive transaction management:
  - Transaction building and signing
  - Multi-chain account management
  - Receipt polling with timeout handling
  - Error handling with detailed logging
- FlashLoanResult model for execution result tracking
- FlashLoanOpportunity model with Id, SourceDex, and TargetDex properties
- Background service for continuous opportunity processing
- Serilog structured logging with console output
- Configuration-based setup for chains, contracts, and services
- Systemd deployment script for Linux production environments

### Changed
- Upgraded Serilog to version 4.3.0 for compatibility
- Upgraded Serilog.Extensions.Hosting to version 10.0.0
- Upgraded Serilog.Sinks.Console to version 6.0.0
- Added Serilog.Settings.Configuration for configuration-based logging
- Added Microsoft.Extensions.Http for HttpClient factory support

### Security
- Private key management via configuration (supports environment variables)
- Transaction simulation before execution to prevent failed transactions
- Comprehensive error handling to prevent fund loss
- Support for AWS Secrets Manager integration (documented)

### Performance
- Asynchronous message processing for high throughput
- Efficient RPC connection pooling via Web3 clients
- Optimized gas cost calculations
- Receipt polling with configurable timeout (2 minutes default)

## [0.1.0] - 2025-12-30

### Added
- Initial project structure
- Core service interfaces and base implementations
- Configuration models for nodes and smart contracts
- Basic NATS subscription setup
- Multi-chain RPC provider foundation

---

## Roadmap

### [1.1.0] - Planned
- Flashbots integration for MEV protection
- MEV-Boost support
- Private transaction relay
- Prometheus metrics endpoint
- Enhanced error recovery mechanisms

### [1.2.0] - Planned
- Tenderly integration for detailed simulation
- Advanced gas estimation algorithms
- Real-time ETH price feed integration
- Multi-transaction bundle execution

### [2.0.0] - Planned
- Support for additional EVM chains (Polygon, Optimism, Avalanche)
- Advanced monitoring and alerting (Grafana dashboards)
- Machine learning model integration for opportunity scoring
- Automated strategy optimization
