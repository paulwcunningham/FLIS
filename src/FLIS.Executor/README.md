# FLIS.Executor - Flash Loan Execution Engine

## Overview

FLIS.Executor is a production-ready, multi-chain flash loan execution service designed to execute profitable MEV opportunities with ML-powered gas bidding and transaction simulation.

## Features

### Multi-Chain Support
- **Ethereum Mainnet** (Chain ID: 1)
- **Base** (Chain ID: 8453)
- **Arbitrum** (Chain ID: 42161)
- Easily extensible to additional EVM-compatible chains

### Core Capabilities

1. **NATS Integration**
   - Subscribes to `magnus.opportunities.flashloan` for real-time opportunities
   - Publishes execution results to `magnus.results.flashloan`
   - Asynchronous message processing

2. **ML-Powered Gas Bidding**
   - Integrates with MLOptimizer service for optimal gas pricing
   - Historical data-driven gas predictions
   - Dynamic gas limit estimation

3. **Transaction Simulation**
   - Pre-execution profitability verification
   - eth_call simulation to prevent failed transactions
   - Gas cost calculation and net profit estimation

4. **Secure Transaction Management**
   - Private key management (supports environment variables and secrets managers)
   - Multi-chain account management
   - Transaction signing and broadcasting

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      FLIS.Executor                              │
│                                                                 │
│  ┌──────────────────┐         ┌─────────────────────┐         │
│  │ NATS Subscriber  │────────▶│ Transaction Manager │         │
│  └──────────────────┘         └─────────────────────┘         │
│                                        │                        │
│                      ┌─────────────────┼────────────────┐      │
│                      ▼                 ▼                ▼      │
│            ┌──────────────┐  ┌──────────────┐  ┌──────────┐   │
│            │ Gas Bidding  │  │ Simulation   │  │   RPC    │   │
│            │   Service    │  │   Service    │  │ Provider │   │
│            └──────────────┘  └──────────────┘  └──────────┘   │
└─────────────────────────────────────────────────────────────────┘
                      │                               │
                      ▼                               ▼
              ┌──────────────┐              ┌──────────────────┐
              │ MLOptimizer  │              │  Multi-Chain     │
              │   Service    │              │  RPC Nodes       │
              └──────────────┘              └──────────────────┘
```

## Services

### NatsOpportunitySubscriber

Background service that:
- Connects to NATS server
- Subscribes to flash loan opportunity messages
- Deserializes opportunities and forwards to TransactionManager

### MultiChainRpcProvider

Manages RPC connections:
- Initializes Web3 clients for each configured chain
- Provides chain-specific RPC access
- Manages node configurations

### GasBiddingService

ML integration for gas optimization:
- Calls MLOptimizer API for gas price predictions
- Provides optimal gas price and limit recommendations
- Estimates transaction costs in USD

### SimulationService

Transaction profitability verification:
- Simulates transactions using eth_call
- Calculates net profit after gas costs
- Prevents unprofitable transaction execution

### TransactionManager

Core execution orchestrator:
1. Receives flash loan opportunities
2. Requests optimal gas bid from GasBiddingService
3. Simulates transaction for profitability
4. Signs and broadcasts profitable transactions
5. Publishes results back to NATS

## Configuration

### appsettings.json

```json
{
  "Nats": {
    "Url": "nats://your-nats-server:4222",
    "OpportunitySubject": "magnus.opportunities.flashloan",
    "ResultSubject": "magnus.results.flashloan"
  },
  "Nodes": [
    {
      "ChainName": "Ethereum",
      "RpcUrl": "http://your-eth-node:8545",
      "ChainId": 1
    }
  ],
  "SmartContracts": [
    {
      "ChainName": "Ethereum",
      "ContractAddress": "0x...",
      "Abi": "[...]"
    }
  ],
  "ExecutorWallet": {
    "PrivateKey": ""  // Set via environment variable
  },
  "MLOptimizer": {
    "BaseUrl": "http://mloptimizer:5000",
    "GasBiddingEndpoint": "/api/v1/gas-bidding"
  }
}
```

### Environment Variables

For production, set sensitive values via environment variables:

```bash
export ExecutorWallet__PrivateKey="0x..."
export Nats__Url="nats://production-nats:4222"
```

### Secrets Management

**CRITICAL:** In production, use AWS Secrets Manager, HashiCorp Vault, or similar:

```csharp
// Example: AWS Secrets Manager integration
var secret = await secretsManager.GetSecretValueAsync(
    new GetSecretValueRequest { SecretId = "magnus/executor/private-key" }
);
```

## Development

### Building

```bash
dotnet build
```

### Running Locally

```bash
dotnet run --project src/FLIS.Executor
```

### Publishing

```bash
dotnet publish -c Release -o ./publish
```

## Deployment

### Systemd Service

Use the provided deployment script:

```bash
sudo ./deploy-executor.sh
```

This creates a systemd service with:
- Automatic restart on failure
- Running as dedicated `flis` user
- Logging via systemd journal
- Production environment configuration

### Docker (Alternative)

Create a Dockerfile:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ .
ENTRYPOINT ["dotnet", "FLIS.Executor.dll"]
```

Build and run:

```bash
docker build -t flis-executor .
docker run -d --name flis-executor \
  -e ExecutorWallet__PrivateKey="0x..." \
  flis-executor
```

## Security Considerations

### Private Key Management

**NEVER** commit private keys to version control:
- Use environment variables
- Use secrets management services
- Rotate keys regularly
- Use hardware security modules (HSM) for production

### Network Security

- Run on private network with firewall rules
- Use TLS for NATS connections
- Whitelist RPC node IPs
- Monitor for unauthorized access attempts

### Transaction Security

- Always simulate before executing
- Set maximum gas price limits
- Implement circuit breakers for rapid failures
- Monitor for unusual transaction patterns

## Monitoring

### Logs

View systemd logs:

```bash
sudo journalctl -u flis-executor -f
```

Key log events:
- Opportunity received
- Gas bid obtained
- Simulation results
- Transaction submission
- Errors and warnings

### Metrics

Monitor:
- Opportunities received per minute
- Execution success rate
- Average profitability
- Gas costs vs predictions
- RPC node response times

## Troubleshooting

### Service Won't Start

Check logs:
```bash
sudo journalctl -u flis-executor -n 100
```

Common issues:
- Missing configuration values
- Invalid private key
- Unreachable NATS server
- Unreachable RPC nodes

### No Opportunities Received

Verify:
- NATS connection is established
- Subscription to correct subject
- Upstream services are publishing opportunities

### Transactions Failing

Check:
- Sufficient balance for gas
- Contract addresses are correct
- RPC nodes are synchronized
- Gas limits are adequate

## Implementation Status

### ✅ Completed (December 30, 2025)

1. **Smart Contract Integration**
   - ✅ Contract ABI integration
   - ✅ executeCrossDexArbitrage implementation
   - ✅ executeMultiHopArbitrage implementation

2. **Transaction Simulation**
   - ✅ Full eth_call implementation
   - ✅ Profitability calculation with gas costs
   - ✅ Flash loan fee estimation (0.09% Aave)

3. **Result Publishing**
   - ✅ NATS result publishing to `magnus.results.flashloan`
   - ✅ Comprehensive result data (profitability, gas used, execution time)
   - ✅ ML feedback loop integration

4. **Transaction Execution**
   - ✅ Transaction building and signing
   - ✅ Multi-chain support (Ethereum, Base, Arbitrum)
   - ✅ Receipt polling with timeout handling
   - ✅ Error handling and result publication

### 🔄 In Progress

- Integration testing on testnets (Sepolia, Base Sepolia, Arbitrum Sepolia)
- Performance optimization for sub-500ms execution

### 📋 Future Enhancements

1. **Advanced Simulation**
   - Tenderly integration for detailed gas estimation
   - Flashbots bundle simulation

2. **Monitoring & Alerting**
   - Prometheus metrics endpoint
   - Grafana dashboards
   - PagerDuty integration for critical failures

3. **MEV Protection**
   - MEV-Boost integration
   - Flashbots Protect support
   - Private transaction relay support

4. **Advanced Features**
   - Multi-transaction bundle execution
   - Dynamic slippage protection
   - Real-time ETH price feed integration

## Contributing

When contributing to FLIS.Executor:

1. **Code Review Required**
   - All changes handling funds require thorough review
   - Security audit for cryptographic operations

2. **Testing**
   - Unit tests for all services
   - Integration tests with testnet
   - Simulation tests with historical data

3. **Documentation**
   - Update README for new features
   - Document configuration changes
   - Add inline comments for complex logic

## License

MIT License - see [LICENSE](../../LICENSE) for details.
