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
├── docker-compose.yml      # Database container configuration
├── db/
│   └── init_db.sql        # Database schema initialization
└── src/
    └── FLIS.DataCollector/ # .NET Worker Service
```

## License

MIT License - see [LICENSE](LICENSE) for details.
