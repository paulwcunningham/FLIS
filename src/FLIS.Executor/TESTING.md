# FLIS.Executor Testing Guide

## Overview

This document provides comprehensive testing instructions for the FLIS.Executor service, including unit testing, integration testing, and end-to-end testing strategies.

## Prerequisites

- .NET 8.0 SDK
- Access to blockchain nodes (Ethereum, Base, Arbitrum)
- NATS server running
- MLOptimizer service running
- FlashLoanArbitrage smart contracts deployed

## Testing Levels

### 1. Unit Testing

Unit tests verify individual components in isolation.

#### Setting Up Unit Tests

```bash
# Create test project
dotnet new xunit -n FLIS.Executor.Tests
cd FLIS.Executor.Tests

# Add project reference
dotnet add reference ../FLIS.Executor/FLIS.Executor.csproj

# Add testing packages
dotnet add package Moq
dotnet add package FluentAssertions
dotnet add package Microsoft.Extensions.Logging.Abstractions
```

#### Example: Testing SimulationService

```csharp
public class SimulationServiceTests
{
    [Fact]
    public async Task SimulateAsync_WithProfitableOpportunity_ReturnsTrue()
    {
        // Arrange
        var mockRpcProvider = new Mock<IMultiChainRpcProvider>();
        var mockConfiguration = new Mock<IConfiguration>();
        var mockLogger = new Mock<ILogger<SimulationService>>();

        var service = new SimulationService(
            mockRpcProvider.Object,
            mockConfiguration.Object,
            mockLogger.Object
        );

        var opportunity = new FlashLoanOpportunity
        {
            ChainName = "Ethereum",
            Asset = "0x...",
            Amount = 1000,
            Strategy = "CrossDex",
            MinProfit = 100
        };

        var gasBid = new GasBidResult(50, 300000, 10);

        // Act
        var (isProfitable, profit) = await service.SimulateAsync(opportunity, gasBid);

        // Assert
        isProfitable.Should().BeTrue();
        profit.Should().BeGreaterThan(0);
    }
}
```

### 2. Integration Testing

Integration tests verify components working together with real dependencies.

#### Testing with Testnet

**Sepolia Ethereum Testnet:**

```bash
# Update appsettings.Development.json
{
  "Nodes": [
    {
      "ChainName": "Sepolia",
      "RpcUrl": "https://rpc.sepolia.org",
      "ChainId": 11155111
    }
  ],
  "SmartContracts": [
    {
      "ChainName": "Sepolia",
      "ContractAddress": "0xYourContractAddress",
      "Abi": "[...]"
    }
  ],
  "ExecutorWallet": {
    "PrivateKey": "0xYourTestnetPrivateKey"
  }
}
```

**Running Integration Tests:**

```bash
# Set environment
export ASPNETCORE_ENVIRONMENT=Development

# Run the service
dotnet run --project src/FLIS.Executor

# Monitor logs
journalctl -u flis-executor -f
```

### 3. End-to-End Testing

E2E tests verify the complete flow from NATS message to on-chain execution.

#### Test Scenario 1: CrossDex Arbitrage

**Step 1: Publish Test Opportunity to NATS**

```bash
# Install NATS CLI
go install github.com/nats-io/natscli/nats@latest

# Publish test opportunity
nats pub magnus.opportunities.flashloan '{
  "id": "test-001",
  "chainName": "Sepolia",
  "asset": "0xTokenAddress",
  "amount": 1000,
  "strategy": "CrossDex",
  "sourceDex": "0xUniswapAddress",
  "targetDex": "0xSushiswapAddress",
  "minProfit": 50,
  "deadline": 1735689600
}'
```

**Step 2: Monitor Execution**

```bash
# Watch logs
tail -f /var/log/flis-executor.log

# Subscribe to results
nats sub magnus.results.flashloan
```

**Step 3: Verify On-Chain**

```bash
# Check transaction on Etherscan
# Verify gas usage vs estimate
# Confirm profit calculations
```

#### Test Scenario 2: MultiHop Arbitrage

```bash
nats pub magnus.opportunities.flashloan '{
  "id": "test-002",
  "chainName": "Sepolia",
  "asset": "0xWETH",
  "amount": 5000,
  "strategy": "MultiHop",
  "path": "0xWETH,0xUSDC,0xDAI,0xWETH",
  "minProfit": 100,
  "deadline": 1735689600
}'
```

### 4. Load Testing

Test the executor under high load conditions.

#### Using K6 for Load Testing

```javascript
// load-test.js
import { check } from 'k6';
import encoding from 'k6/encoding';

export const options = {
  vus: 10, // Virtual users
  duration: '5m',
};

export default function () {
  const opportunity = {
    id: `test-${__VU}-${__ITER}`,
    chainName: 'Sepolia',
    asset: '0xTokenAddress',
    amount: 1000,
    strategy: 'CrossDex',
    sourceDex: '0xDex1',
    targetDex: '0xDex2',
    minProfit: 50,
    deadline: Date.now() + 300000
  };

  // Publish to NATS
  const payload = encoding.b64encode(JSON.stringify(opportunity));

  // Monitor results
  check(result, {
    'processing completed': (r) => r.success !== undefined,
  });
}
```

Run the load test:

```bash
k6 run load-test.js
```

### 5. Security Testing

#### Private Key Security

```bash
# Verify private key is not in logs
grep -r "0x" /var/log/flis-executor.log

# Check environment variable handling
printenv | grep ExecutorWallet

# Ensure secrets are encrypted at rest
```

#### Transaction Simulation

```bash
# Test unprofitable rejection
# Opportunity with gas cost > profit should be rejected

nats pub magnus.opportunities.flashloan '{
  "id": "security-test-001",
  "chainName": "Sepolia",
  "asset": "0xToken",
  "amount": 10,
  "strategy": "CrossDex",
  "sourceDex": "0xDex1",
  "targetDex": "0xDex2",
  "minProfit": 1,
  "deadline": 1735689600
}'

# Expected: Transaction should NOT be submitted
# Check logs for "Skipping unprofitable opportunity"
```

### 6. Performance Testing

#### Metrics to Monitor

1. **Latency**
   - Time from NATS message received to simulation complete
   - Time from simulation to transaction submission
   - Total processing time

2. **Throughput**
   - Opportunities processed per minute
   - Successful executions per hour

3. **Resource Usage**
   - CPU utilization
   - Memory consumption
   - Network I/O

#### Performance Test Setup

```bash
# Enable detailed logging
export Serilog__MinimumLevel__Default=Debug

# Monitor with Prometheus (if integrated)
curl http://localhost:9090/metrics

# Monitor system resources
htop
netstat -an | grep ESTABLISHED
```

### 7. Chaos Engineering

Test resilience under failure conditions.

#### RPC Node Failure

```bash
# Stop RPC node temporarily
docker stop ethereum-node

# Verify service handles failure gracefully
# Should log error and skip opportunity
```

#### NATS Disconnection

```bash
# Restart NATS server
systemctl restart nats-server

# Verify automatic reconnection
# Check connection state in logs
```

#### MLOptimizer Unavailability

```bash
# Stop MLOptimizer service
systemctl stop mloptimizer

# Verify graceful degradation
# Should log error but not crash
```

## Test Data

### Sample Opportunities

**Profitable CrossDex:**
```json
{
  "id": "prof-cross-001",
  "chainName": "Ethereum",
  "asset": "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2",
  "amount": 10000,
  "strategy": "CrossDex",
  "sourceDex": "0x7a250d5630B4cF539739dF2C5dAcb4c659F2488D",
  "targetDex": "0xd9e1cE17f2641f24aE83637ab66a2cca9C378B9F",
  "minProfit": 500,
  "deadline": 1735689600
}
```

**Unprofitable MultiHop:**
```json
{
  "id": "unprof-multi-001",
  "chainName": "Ethereum",
  "asset": "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2",
  "amount": 100,
  "strategy": "MultiHop",
  "path": "0xWETH,0xUSDC,0xWETH",
  "minProfit": 5,
  "deadline": 1735689600
}
```

## Continuous Integration

### GitHub Actions Workflow

```yaml
name: FLIS.Executor CI

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v3

    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '8.0.x'

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --no-restore

    - name: Run tests
      run: dotnet test --no-build --verbosity normal

    - name: Publish code coverage
      uses: codecov/codecov-action@v3
```

## Test Checklist

Before deploying to production:

- [ ] All unit tests pass
- [ ] Integration tests pass on testnet
- [ ] E2E test with real NATS messages successful
- [ ] Load test shows acceptable performance
- [ ] Security tests pass (no key leaks, simulation works)
- [ ] Chaos tests show graceful degradation
- [ ] Performance metrics within acceptable ranges
- [ ] Code coverage > 80%
- [ ] No critical or high severity security vulnerabilities
- [ ] Documentation updated

## Troubleshooting Tests

### Common Issues

**Test fails with "NATS connection timeout"**
```bash
# Check NATS server is running
systemctl status nats-server

# Verify firewall allows connection
netstat -an | grep 4222
```

**Simulation always returns unprofitable**
```bash
# Check gas bid service response
curl -X POST http://mloptimizer:5000/api/v1/gas-bidding \
  -H "Content-Type: application/json" \
  -d '{"chainName":"Ethereum","asset":"0x...","amount":1000}'

# Verify contract ABI is correct
cat appsettings.json | jq '.SmartContracts[0].Abi'
```

**Transaction fails on-chain**
```bash
# Check account has sufficient balance
cast balance 0xYourExecutorAddress --rpc-url $RPC_URL

# Verify contract is deployed
cast code 0xContractAddress --rpc-url $RPC_URL

# Check gas price is not too low
cast gas-price --rpc-url $RPC_URL
```

## Next Steps

1. Implement automated test suite
2. Set up continuous integration pipeline
3. Deploy to staging environment
4. Run extended load tests
5. Security audit before production
6. Set up monitoring and alerting
7. Document production deployment procedure

## Resources

- [Nethereum Documentation](https://docs.nethereum.com/)
- [NATS.io Testing Guide](https://docs.nats.io/nats-concepts/testing)
- [xUnit Documentation](https://xunit.net/docs/getting-started)
- [Moq Quickstart](https://github.com/moq/moq4/wiki/Quickstart)
