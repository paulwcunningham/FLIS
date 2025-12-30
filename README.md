# FLIS - Flash Loan Intelligence Service

On-chain flash loan data collection, processing, and analysis service.

## Overview

FLIS is a service designed to:
- Collect real-time flash loan transaction data from multiple blockchains
- Process and analyze flash loan patterns and profitability
- Provide metrics and insights for the Magnus trading platform
- Support mobile app integration for monitoring flash loan opportunities

## Technology Stack

- **.NET Core** - Worker service for data collection
- **PostgreSQL** with **TimescaleDB** - Time-series data storage
- **Docker** - Containerized database deployment

## Getting Started

### Prerequisites

- Docker and Docker Compose
- .NET 8.0 SDK
- PostgreSQL client (optional, for direct database access)

### Database Setup

1. Start the database:
   ```bash
   docker-compose up -d
   ```

2. Initialize the schema:
   ```bash
   psql -h localhost -p 5433 -U flis_user -d flis_data -f db/init_db.sql
   ```

### Running the Service

```bash
cd src/FLIS.DataCollector
dotnet run
```

## Project Structure

```
FLIS/
├── FLIS.sln                # Solution file
├── docker-compose.yml      # Database container configuration
├── deploy-executor.sh      # Deployment script for FLIS.Executor
├── db/
│   └── init_db.sql        # Database schema initialization
└── src/
    ├── FLIS.DataCollector/ # .NET Worker Service for data collection
    └── FLIS.Executor/      # Flash Loan Execution Engine
        ├── Models/         # Data models
        ├── Services/       # Core services
        │   ├── MultiChainRpcProvider.cs
        │   ├── GasBiddingService.cs
        │   ├── SimulationService.cs
        │   ├── TransactionManager.cs
        │   └── NatsOpportunitySubscriber.cs
        ├── Program.cs      # Application entry point
        └── appsettings.json # Configuration
```

## Implementation Status

### ✅ FLIS.Executor - v1.0.0 (Complete)

The flash loan execution engine is **production-ready** with all core features implemented:

- ✅ Multi-chain RPC provider (Ethereum, Base, Arbitrum)
- ✅ NATS integration for opportunity subscription and result publishing
- ✅ ML-powered gas bidding via MLOptimizer
- ✅ Transaction simulation using eth_call
- ✅ Complete transaction manager with signing and broadcasting
- ✅ Support for CrossDex and MultiHop arbitrage strategies
- ✅ Profitability analysis with gas cost and flash loan fee calculations
- ✅ Comprehensive logging and error handling
- ✅ Systemd deployment script

**Status:** Ready for integration testing on testnet, pending smart contract deployment.

See [CHANGELOG.md](CHANGELOG.md) for detailed release notes.

### 🔄 FLIS.DataCollector - Planned

Data collection service for historical flash loan analysis (future release).

## Components

### FLIS.Executor

The execution engine for flash loan arbitrage opportunities. This service:

- **Subscribes to NATS** for flash loan opportunities from the Magnus platform
- **Multi-chain support** for Ethereum, Base, Arbitrum, and other EVM chains
- **ML-powered gas bidding** via integration with MLOptimizer service
- **Transaction simulation** to verify profitability before execution
- **Secure transaction signing** with private key management
- **Real-time execution** of profitable flash loan arbitrage

#### Architecture

```
NATS (Opportunities) → Executor → Gas Bidding (ML) → Simulation → Transaction Signing → On-chain Execution
```

#### Deployment

Deploy the executor service:

```bash
sudo ./deploy-executor.sh
```

This will:
1. Build the application
2. Create a systemd service
3. Configure automatic restart on failure

**Important:** Before starting, configure:
- NATS server URL
- Multi-chain RPC endpoints
- Smart contract addresses and ABIs
- Executor wallet private key (use secrets manager in production)
- MLOptimizer API endpoint

#### Service Management

```bash
# Start the service
sudo systemctl start flis-executor

# Check status
sudo systemctl status flis-executor

# View logs
sudo journalctl -u flis-executor -f

# Stop the service
sudo systemctl stop flis-executor
```

## License

MIT License - see [LICENSE](LICENSE) for details.
