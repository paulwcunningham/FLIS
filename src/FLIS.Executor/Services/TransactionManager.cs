using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Hex.HexTypes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NATS.Client;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FLIS.Executor.Services;

public interface ITransactionManager
{
    Task ProcessOpportunityAsync(FlashLoanOpportunity opportunity);
    void SetNatsConnection(IConnection connection);
}

public class TransactionManager : ITransactionManager
{
    private readonly IMultiChainRpcProvider _rpcProvider;
    private readonly IGasBiddingService _gasBiddingService;
    private readonly ISimulationService _simulationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransactionManager> _logger;
    private readonly string _privateKey;
    private IConnection? _natsConnection;

    public TransactionManager(
        IMultiChainRpcProvider rpcProvider,
        IGasBiddingService gasBiddingService,
        ISimulationService simulationService,
        IConfiguration configuration,
        ILogger<TransactionManager> logger)
    {
        _rpcProvider = rpcProvider;
        _gasBiddingService = gasBiddingService;
        _simulationService = simulationService;
        _configuration = configuration;
        _logger = logger;

        // In production, retrieve from AWS Secrets Manager
        _privateKey = configuration["ExecutorWallet:PrivateKey"]
            ?? throw new InvalidOperationException("Executor private key not configured");
    }

    public void SetNatsConnection(IConnection connection)
    {
        _natsConnection = connection;
    }

    public async Task ProcessOpportunityAsync(FlashLoanOpportunity opportunity)
    {
        _logger.LogInformation(
            "Processing opportunity {Id} on {Chain}: {Strategy} {Asset} {Amount}",
            opportunity.Id,
            opportunity.ChainName,
            opportunity.Strategy,
            opportunity.Asset,
            opportunity.Amount
        );

        try
        {
            // 1. Get optimal gas bid from MLOptimizer
            var gasBid = await _gasBiddingService.GetOptimalGasBidAsync(opportunity);
            _logger.LogInformation(
                "Gas bid received: {GasPrice} gwei, Limit: {GasLimit}, EstCost: ${Cost:F4}",
                gasBid.GasPriceGwei,
                gasBid.GasLimit,
                gasBid.EstimatedCostUsd
            );

            // 2. Simulate the trade to confirm profitability
            var (isProfitable, estimatedProfit) = await _simulationService.SimulateAsync(opportunity, gasBid);
            if (!isProfitable)
            {
                _logger.LogWarning(
                    "Skipping unprofitable opportunity {Id} on {Chain}. Estimated profit: ${Profit:F4}",
                    opportunity.Id,
                    opportunity.ChainName,
                    estimatedProfit
                );

                await PublishResultAsync(new FlashLoanResult
                {
                    OpportunityId = opportunity.Id,
                    ChainName = opportunity.ChainName,
                    Strategy = opportunity.Strategy,
                    Asset = opportunity.Asset,
                    Amount = opportunity.Amount,
                    Success = false,
                    Reason = "Unprofitable after gas costs",
                    Timestamp = DateTime.UtcNow
                });

                return;
            }

            _logger.LogInformation(
                "Opportunity {Id} is profitable: ${Profit:F4} USD",
                opportunity.Id,
                estimatedProfit
            );

            // 3. Get contract configuration
            var contractConfig = _configuration.GetSection("SmartContracts")
                .Get<List<SmartContractConfig>>()
                ?.FirstOrDefault(c => c.ChainName == opportunity.ChainName);

            if (contractConfig == null)
            {
                _logger.LogError("No contract configuration found for chain {ChainName}", opportunity.ChainName);
                await PublishResultAsync(new FlashLoanResult
                {
                    OpportunityId = opportunity.Id,
                    ChainName = opportunity.ChainName,
                    Strategy = opportunity.Strategy,
                    Asset = opportunity.Asset,
                    Amount = opportunity.Amount,
                    Success = false,
                    Reason = "No contract configuration",
                    Timestamp = DateTime.UtcNow
                });
                return;
            }

            // 4. Get the right RPC provider and account
            var nodeConfig = _rpcProvider.GetNodeConfig(opportunity.ChainName);
            var account = new Account(_privateKey, nodeConfig.ChainId);
            var web3 = new Web3(account, nodeConfig.RpcUrl);

            // 5. Build the transaction
            var contract = web3.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);

            Function? function = null;
            object[]? parameters = null;

            if (opportunity.Strategy == "CrossDex" &&
                !string.IsNullOrEmpty(opportunity.SourceDex) &&
                !string.IsNullOrEmpty(opportunity.TargetDex))
            {
                function = contract.GetFunction("executeCrossDexArbitrage");
                parameters = new object[]
                {
                    opportunity.Asset,
                    Web3.Convert.ToWei(opportunity.Amount),
                    opportunity.SourceDex,
                    opportunity.TargetDex,
                    Web3.Convert.ToWei(opportunity.MinProfit)
                };
            }
            else if (opportunity.Strategy == "MultiHop" && !string.IsNullOrEmpty(opportunity.Path))
            {
                function = contract.GetFunction("executeMultiHopArbitrage");
                var pathAddresses = opportunity.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);

                parameters = new object[]
                {
                    opportunity.Asset,
                    Web3.Convert.ToWei(opportunity.Amount),
                    pathAddresses,
                    Web3.Convert.ToWei(opportunity.MinProfit)
                };
            }
            else
            {
                _logger.LogWarning("Invalid strategy or missing parameters: {Strategy}", opportunity.Strategy);
                await PublishResultAsync(new FlashLoanResult
                {
                    OpportunityId = opportunity.Id,
                    ChainName = opportunity.ChainName,
                    Strategy = opportunity.Strategy,
                    Asset = opportunity.Asset,
                    Amount = opportunity.Amount,
                    Success = false,
                    Reason = "Invalid strategy or missing parameters",
                    Timestamp = DateTime.UtcNow
                });
                return;
            }

            // 6. Submit the transaction
            _logger.LogInformation("Submitting transaction {Id} to {Chain}...", opportunity.Id, opportunity.ChainName);

            var gasPrice = new HexBigInteger(Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei));
            var gasLimit = new HexBigInteger(gasBid.GasLimit);

            var txHash = await function.SendTransactionAsync(
                account.Address,
                gasLimit,
                gasPrice,
                new HexBigInteger(0), // value
                parameters
            );

            _logger.LogInformation("Transaction submitted: {TxHash}", txHash);

            // 7. Wait for transaction receipt
            var receipt = await WaitForReceiptAsync(web3, txHash, 60);

            if (receipt == null)
            {
                _logger.LogWarning("Transaction receipt not available after timeout: {TxHash}", txHash);

                await PublishResultAsync(new FlashLoanResult
                {
                    OpportunityId = opportunity.Id,
                    ChainName = opportunity.ChainName,
                    Strategy = opportunity.Strategy,
                    Asset = opportunity.Asset,
                    Amount = opportunity.Amount,
                    Success = false,
                    Reason = "Receipt timeout",
                    TransactionHash = txHash,
                    Timestamp = DateTime.UtcNow
                });

                return;
            }

            // 8. Check if transaction succeeded
            var success = receipt.Status?.Value == 1;

            if (success)
            {
                _logger.LogInformation(
                    "Transaction successful: {TxHash}, Gas Used: {GasUsed}",
                    txHash,
                    receipt.GasUsed?.Value
                );
            }
            else
            {
                _logger.LogError("Transaction failed: {TxHash}", txHash);
            }

            // 9. Calculate actual costs and profit
            var actualGasUsed = receipt.GasUsed?.Value ?? 0;
            var actualGasPriceWei = receipt.EffectiveGasPrice?.Value ?? gasPrice.Value;
            var actualGasPriceGwei = Web3.Convert.FromWei(actualGasPriceWei, Nethereum.Util.UnitConversion.EthUnit.Gwei);
            var actualCostWei = actualGasUsed * actualGasPriceWei;
            var actualCostEth = Web3.Convert.FromWei(actualCostWei);
            // TODO: Get actual ETH price for accurate USD cost
            var actualCostUsd = (decimal)actualCostEth * 2000m; // Placeholder ETH price

            // 10. Publish result to NATS for MLOptimizer
            await PublishResultAsync(new FlashLoanResult
            {
                OpportunityId = opportunity.Id,
                ChainName = opportunity.ChainName,
                Strategy = opportunity.Strategy,
                Asset = opportunity.Asset,
                Amount = opportunity.Amount,
                Success = success,
                TransactionHash = txHash,
                GasUsed = (long)actualGasUsed,
                GasPriceGwei = actualGasPriceGwei,
                ActualCostUsd = actualCostUsd,
                ActualProfitUsd = success ? estimatedProfit - actualCostUsd : null,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing opportunity {Id}: {Message}",
                opportunity.Id, ex.Message);

            await PublishResultAsync(new FlashLoanResult
            {
                OpportunityId = opportunity.Id,
                ChainName = opportunity.ChainName,
                Strategy = opportunity.Strategy,
                Asset = opportunity.Asset,
                Amount = opportunity.Amount,
                Success = false,
                Reason = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private async Task<TransactionReceipt?> WaitForReceiptAsync(Web3 web3, string txHash, int maxAttempts)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                if (receipt != null)
                {
                    return receipt;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking receipt (attempt {Attempt}/{Max})", i + 1, maxAttempts);
            }

            await Task.Delay(2000); // Wait 2 seconds between attempts
        }

        return null;
    }

    private async Task PublishResultAsync(FlashLoanResult result)
    {
        try
        {
            var resultSubject = _configuration["Nats:ResultSubject"] ?? "magnus.results.flashloan";

            if (_natsConnection == null || _natsConnection.State != ConnState.CONNECTED)
            {
                _logger.LogWarning("NATS connection not available, cannot publish result");
                return;
            }

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var data = Encoding.UTF8.GetBytes(json);
            _natsConnection.Publish(resultSubject, data);

            _logger.LogInformation(
                "Published result to NATS: {Subject}, Success: {Success}, TxHash: {TxHash}",
                resultSubject,
                result.Success,
                result.TransactionHash
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish result to NATS");
        }
    }
}
