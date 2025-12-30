using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Hex.HexTypes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NATS.Client;
using FLIS.Executor.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FLIS.Executor.Services;

public interface ITransactionManager
{
    Task ProcessOpportunityAsync(FlashLoanOpportunity opportunity);
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

        // Initialize NATS connection
        try
        {
            var natsUrl = configuration["Nats:Url"];
            if (!string.IsNullOrEmpty(natsUrl))
            {
                var factory = new ConnectionFactory();
                _natsConnection = factory.CreateConnection(natsUrl);
                _logger.LogInformation("Connected to NATS at {Url}", natsUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to NATS - results will not be published");
        }
    }

    public async Task ProcessOpportunityAsync(FlashLoanOpportunity opportunity)
    {
        _logger.LogInformation(
            "Processing opportunity {Id} on {Chain}: {Strategy} {Asset} {Amount}",
            opportunity.Id, opportunity.ChainName, opportunity.Strategy, opportunity.Asset, opportunity.Amount);

        try
        {
            // 1. Get optimal gas bid from MLOptimizer
            var gasBid = await _gasBiddingService.GetOptimalGasBidAsync(opportunity);
            _logger.LogInformation("Gas bid: {GasPrice} gwei, Limit: {GasLimit}, Estimated Cost: {Cost:C}",
                gasBid.GasPriceGwei, gasBid.GasLimit, gasBid.EstimatedCostUsd);

            // 2. Simulate the trade to confirm profitability
            var (isProfitable, estimatedProfit) = await _simulationService.SimulateAsync(opportunity, gasBid);
            if (!isProfitable)
            {
                _logger.LogWarning("Skipping unprofitable opportunity on {Chain}. Estimated profit: {Profit:C}",
                    opportunity.ChainName, estimatedProfit);

                // Publish negative result to NATS
                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: false,
                    Reason: "Unprofitable after gas costs",
                    TransactionHash: null,
                    GasUsed: null,
                    GasPrice: null,
                    ActualCostUsd: null,
                    Timestamp: DateTime.UtcNow
                ));

                return;
            }

            _logger.LogInformation("Opportunity is profitable: {Profit:C} USD", estimatedProfit);

            // 3. Get the right RPC provider and account
            var web3 = _rpcProvider.GetWeb3(opportunity.ChainName);
            var nodeConfig = _rpcProvider.GetNodeConfig(opportunity.ChainName);
            var account = new Account(_privateKey, nodeConfig.ChainId);
            var web3WithAccount = new Web3(account, web3.Client);

            // 4. Get the contract configuration
            var contractConfigs = _configuration.GetSection("SmartContracts")
                .Get<List<SmartContractConfig>>();

            var contractConfig = contractConfigs?
                .FirstOrDefault(c => c.ChainName == opportunity.ChainName);

            if (contractConfig == null)
            {
                _logger.LogError("No contract configuration found for chain {ChainName}", opportunity.ChainName);

                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: false,
                    Reason: "No contract configuration found",
                    TransactionHash: null,
                    GasUsed: null,
                    GasPrice: null,
                    ActualCostUsd: null,
                    Timestamp: DateTime.UtcNow
                ));

                return;
            }

            var contract = web3WithAccount.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);

            // 5. Build and sign the transaction
            Function function;
            object[] parameters;

            if (opportunity.Strategy == "CrossDex" && opportunity.SourceDex != null && opportunity.TargetDex != null)
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
            else if (opportunity.Strategy == "MultiHop" && opportunity.Path != null)
            {
                function = contract.GetFunction("executeMultiHopArbitrage");
                parameters = new object[]
                {
                    opportunity.Asset,
                    Web3.Convert.ToWei(opportunity.Amount),
                    opportunity.Path.Split(','),
                    Web3.Convert.ToWei(opportunity.MinProfit)
                };
            }
            else
            {
                _logger.LogWarning("Unknown strategy or missing parameters: {Strategy}", opportunity.Strategy);

                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: false,
                    Reason: "Unknown strategy or missing parameters",
                    TransactionHash: null,
                    GasUsed: null,
                    GasPrice: null,
                    ActualCostUsd: null,
                    Timestamp: DateTime.UtcNow
                ));

                return;
            }

            // Convert gas price from Gwei to Wei
            var gasPriceWei = Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei);

            // Create transaction input
            var transactionInput = function.CreateTransactionInput(
                account.Address,
                new HexBigInteger(gasBid.GasLimit),
                new HexBigInteger(gasPriceWei),
                new HexBigInteger(0), // value
                parameters
            );

            // 6. Submit the transaction
            _logger.LogInformation("Submitting transaction to {ChainName}...", opportunity.ChainName);

            var txHash = await web3WithAccount.Eth.Transactions.SendTransaction.SendRequestAsync(transactionInput);

            _logger.LogInformation("Transaction submitted: {TxHash}", txHash);

            // 7. Wait for transaction receipt (with timeout)
            TransactionReceipt? receipt = null;
            int attempts = 0;
            const int maxAttempts = 60; // 2 minutes with 2-second intervals

            while (receipt == null && attempts < maxAttempts)
            {
                await Task.Delay(2000); // Wait 2 seconds
                receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                attempts++;
            }

            if (receipt == null)
            {
                _logger.LogWarning("Transaction receipt not available after {Seconds} seconds: {TxHash}",
                    maxAttempts * 2, txHash);

                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: false,
                    Reason: "Receipt timeout",
                    TransactionHash: txHash,
                    GasUsed: null,
                    GasPrice: gasPriceWei.ToString(),
                    ActualCostUsd: null,
                    Timestamp: DateTime.UtcNow
                ));

                return;
            }

            // 8. Check if transaction succeeded
            var success = receipt.Status?.Value == 1;

            if (success)
            {
                var actualGasCost = Web3.Convert.FromWei(gasPriceWei * receipt.GasUsed.Value);
                // TODO: Get real-time ETH price for accurate USD cost
                var actualCostUsd = gasBid.EstimatedCostUsd; // Approximation

                _logger.LogInformation(
                    "Transaction successful: {TxHash}, Gas Used: {GasUsed}, Cost: {Cost:C}",
                    txHash,
                    receipt.GasUsed.Value,
                    actualCostUsd
                );

                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: true,
                    Reason: null,
                    TransactionHash: txHash,
                    GasUsed: (long)receipt.GasUsed.Value,
                    GasPrice: gasPriceWei.ToString(),
                    ActualCostUsd: actualCostUsd,
                    Timestamp: DateTime.UtcNow
                ));
            }
            else
            {
                _logger.LogError("Transaction failed: {TxHash}", txHash);

                await PublishResultAsync(new FlashLoanResult(
                    OpportunityId: opportunity.Id,
                    ChainName: opportunity.ChainName,
                    Strategy: opportunity.Strategy,
                    Asset: opportunity.Asset,
                    Amount: opportunity.Amount,
                    Success: false,
                    Reason: "Transaction reverted",
                    TransactionHash: txHash,
                    GasUsed: (long)receipt.GasUsed.Value,
                    GasPrice: gasPriceWei.ToString(),
                    ActualCostUsd: gasBid.EstimatedCostUsd,
                    Timestamp: DateTime.UtcNow
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process opportunity {Id} on {Chain}",
                opportunity.Id, opportunity.ChainName);

            await PublishResultAsync(new FlashLoanResult(
                OpportunityId: opportunity.Id,
                ChainName: opportunity.ChainName,
                Strategy: opportunity.Strategy,
                Asset: opportunity.Asset,
                Amount: opportunity.Amount,
                Success: false,
                Reason: ex.Message,
                TransactionHash: null,
                GasUsed: null,
                GasPrice: null,
                ActualCostUsd: null,
                Timestamp: DateTime.UtcNow
            ));
        }
    }

    private async Task PublishResultAsync(FlashLoanResult result)
    {
        try
        {
            if (_natsConnection == null)
            {
                _logger.LogWarning("NATS connection not available - skipping result publication");
                return;
            }

            var json = JsonSerializer.Serialize(result);
            var data = Encoding.UTF8.GetBytes(json);

            var resultSubject = _configuration["Nats:ResultSubject"]
                ?? "magnus.results.flashloan";

            _natsConnection.Publish(resultSubject, data);

            _logger.LogInformation(
                "Published result to NATS: {Subject} - OpportunityId={Id}, Success={Success}",
                resultSubject, result.OpportunityId, result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish result to NATS");
        }
    }
}
