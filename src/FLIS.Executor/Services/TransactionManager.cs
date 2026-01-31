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
using FLIS.Executor.Services.MEV;
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
    private readonly IResultPublisher _resultPublisher;
    private readonly IMevCoordinator _mevCoordinator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransactionManager> _logger;
    private readonly string _privateKey;

    // Timing tracking for latency metrics
    private long _opportunityReceivedAt;
    private long _simulationStartedAt;
    private long _simulationCompletedAt;
    private long _transactionSubmittedAt;

    public TransactionManager(
        IMultiChainRpcProvider rpcProvider,
        IGasBiddingService gasBiddingService,
        ISimulationService simulationService,
        IResultPublisher resultPublisher,
        IMevCoordinator mevCoordinator,
        IConfiguration configuration,
        ILogger<TransactionManager> logger)
    {
        _rpcProvider = rpcProvider;
        _gasBiddingService = gasBiddingService;
        _simulationService = simulationService;
        _resultPublisher = resultPublisher;
        _mevCoordinator = mevCoordinator;
        _configuration = configuration;
        _logger = logger;

        // In production, retrieve from AWS Secrets Manager
        _privateKey = configuration["ExecutorWallet:PrivateKey"]
            ?? throw new InvalidOperationException("Executor private key not configured");
    }

    public async Task ProcessOpportunityAsync(FlashLoanOpportunity opportunity)
    {
        _opportunityReceivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        
        _logger.LogInformation(
            "Processing opportunity {Id} on {Chain}: {Strategy} {Asset} {Amount}, AOI={AOI:F2}, Confidence={Conf:F2}",
            opportunity.Id, opportunity.ChainName, opportunity.Strategy, 
            opportunity.Asset, opportunity.Amount,
            opportunity.AoiScore ?? 0, opportunity.ConfidenceScore);

        // Publish status update
        await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
            opportunity.Id, "received", DateTime.UtcNow, null));

        try
        {
            // 1. Get optimal gas bid from MLOptimizer
            var gasBid = await _gasBiddingService.GetOptimalGasBidAsync(opportunity);
            _logger.LogInformation("Gas bid: {GasPrice} gwei, Limit: {GasLimit}, Estimated Cost: {Cost:C}",
                gasBid.GasPriceGwei, gasBid.GasLimit, gasBid.EstimatedCostUsd);

            // 2. Simulate the trade to confirm profitability
            await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
                opportunity.Id, "simulating", DateTime.UtcNow, null));
            
            _simulationStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var (isProfitable, estimatedProfit) = await _simulationService.SimulateAsync(opportunity, gasBid);
            _simulationCompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            
            if (!isProfitable)
            {
                _logger.LogWarning("Skipping unprofitable opportunity on {Chain}. Estimated profit: {Profit:C}",
                    opportunity.ChainName, estimatedProfit);

                await PublishEnhancedResultAsync(opportunity, gasBid, false, 
                    "Unprofitable after gas costs", null, null, estimatedProfit, null);
                return;
            }

            _logger.LogInformation("Opportunity is profitable: {Profit:C} USD", estimatedProfit);

            // 3. Check if we should use MEV
            if (opportunity.UseMev && _mevCoordinator.IsMevAvailable(opportunity.ChainName))
            {
                await ProcessWithMevAsync(opportunity, gasBid, estimatedProfit);
            }
            else
            {
                await ProcessStandardAsync(opportunity, gasBid, estimatedProfit);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process opportunity {Id} on {Chain}",
                opportunity.Id, opportunity.ChainName);

            await PublishEnhancedResultAsync(opportunity, null, false, ex.Message, null, null, null, null);
        }
    }

    private async Task ProcessWithMevAsync(FlashLoanOpportunity opportunity, GasBid gasBid, decimal estimatedProfit)
    {
        _logger.LogInformation("Processing opportunity {Id} via MEV ({Provider})",
            opportunity.Id, opportunity.PreferredMevProvider ?? "auto");

        await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
            opportunity.Id, "submitting_mev", DateTime.UtcNow, 
            $"Provider: {opportunity.PreferredMevProvider ?? "auto"}"));

        // Build the transaction(s)
        var transactions = await BuildTransactionsAsync(opportunity, gasBid);
        
        if (transactions == null || transactions.Count == 0)
        {
            await PublishEnhancedResultAsync(opportunity, gasBid, false,
                "Failed to build transactions", null, null, estimatedProfit, null);
            return;
        }

        _transactionSubmittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

        // Execute via MEV coordinator
        var mevResult = await _mevCoordinator.ExecuteWithMevAsync(opportunity, transactions);

        var confirmedAt = mevResult.ConfirmedAtNanos;

        // Publish enhanced result
        await PublishEnhancedResultAsync(
            opportunity, gasBid, mevResult.Success, mevResult.Reason,
            mevResult.TransactionHashes.FirstOrDefault(),
            mevResult.BlockNumber.HasValue ? (long?)mevResult.BlockNumber.Value : null,
            estimatedProfit, mevResult.ProfitRealized,
            mevProvider: mevResult.Provider,
            bundleId: mevResult.BundleId,
            mevTipPaid: mevResult.TipPaid,
            wasFrontrun: mevResult.WasFrontrun,
            wasBackrun: mevResult.WasBackrun,
            confirmedAtNanos: confirmedAt);
    }

    private async Task ProcessStandardAsync(FlashLoanOpportunity opportunity, GasBid gasBid, decimal estimatedProfit)
    {
        await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
            opportunity.Id, "submitting", DateTime.UtcNow, null));

        // Get the right RPC provider and account
        var web3 = _rpcProvider.GetWeb3(opportunity.ChainName);
        var nodeConfig = _rpcProvider.GetNodeConfig(opportunity.ChainName);
        var account = new Account(_privateKey, nodeConfig.ChainId);
        var web3WithAccount = new Web3(account, web3.Client);

        // Get the contract configuration
        var contractConfigs = _configuration.GetSection("SmartContracts")
            .Get<List<SmartContractConfig>>();

        var contractConfig = contractConfigs?
            .FirstOrDefault(c => c.ChainName == opportunity.ChainName);

        if (contractConfig == null)
        {
            _logger.LogError("No contract configuration found for chain {ChainName}", opportunity.ChainName);
            await PublishEnhancedResultAsync(opportunity, gasBid, false,
                "No contract configuration found", null, null, estimatedProfit, null);
            return;
        }

        var contract = web3WithAccount.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);

        // Build and sign the transaction
        var (function, parameters) = BuildFunctionCall(opportunity, contract);
        
        if (function == null)
        {
            await PublishEnhancedResultAsync(opportunity, gasBid, false,
                "Unknown strategy or missing parameters", null, null, estimatedProfit, null);
            return;
        }

        // Convert gas price from Gwei to Wei
        var gasPriceWei = Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei);

        // Create transaction input
        var transactionInput = function.CreateTransactionInput(
            account.Address,
            new HexBigInteger(gasBid.GasLimit),
            new HexBigInteger(gasPriceWei),
            new HexBigInteger(0),
            parameters!
        );

        // Submit the transaction
        _logger.LogInformation("Submitting transaction to {ChainName}...", opportunity.ChainName);
        _transactionSubmittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

        var txHash = await web3WithAccount.Eth.Transactions.SendTransaction.SendRequestAsync(transactionInput);
        _logger.LogInformation("Transaction submitted: {TxHash}", txHash);

        await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
            opportunity.Id, "pending", DateTime.UtcNow, txHash));

        // Wait for transaction receipt
        var receipt = await WaitForReceiptAsync(web3, txHash);
        var confirmedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;

        if (receipt == null)
        {
            _logger.LogWarning("Transaction receipt not available: {TxHash}", txHash);
            await PublishEnhancedResultAsync(opportunity, gasBid, false,
                "Receipt timeout", txHash, null, estimatedProfit, null);
            return;
        }

        // Check if transaction succeeded
        var success = receipt.Status?.Value == 1;
        var gasUsed = (long)receipt.GasUsed.Value;
        var actualCostUsd = gasBid.EstimatedCostUsd * ((decimal)gasUsed / gasBid.GasLimit);

        if (success)
        {
            _logger.LogInformation(
                "Transaction successful: {TxHash}, Gas Used: {GasUsed}, Cost: {Cost:C}",
                txHash, gasUsed, actualCostUsd);
        }
        else
        {
            _logger.LogError("Transaction failed: {TxHash}", txHash);
        }

        await PublishEnhancedResultAsync(
            opportunity, gasBid, success,
            success ? null : "Transaction reverted",
            txHash, (long)receipt.BlockNumber.Value,
            estimatedProfit, success ? estimatedProfit - actualCostUsd : 0,
            gasUsed: gasUsed,
            gasPriceWei: gasPriceWei.ToString(),
            actualCostUsd: actualCostUsd,
            blockHash: receipt.BlockHash,
            transactionIndex: (int)receipt.TransactionIndex.Value,
            confirmedAtNanos: confirmedAt);
    }

    private async Task<List<string>?> BuildTransactionsAsync(FlashLoanOpportunity opportunity, GasBid gasBid)
    {
        try
        {
            var web3 = _rpcProvider.GetWeb3(opportunity.ChainName);
            var nodeConfig = _rpcProvider.GetNodeConfig(opportunity.ChainName);
            var account = new Account(_privateKey, nodeConfig.ChainId);
            var web3WithAccount = new Web3(account, web3.Client);

            var contractConfigs = _configuration.GetSection("SmartContracts")
                .Get<List<SmartContractConfig>>();

            var contractConfig = contractConfigs?
                .FirstOrDefault(c => c.ChainName == opportunity.ChainName);

            if (contractConfig == null)
            {
                return null;
            }

            var contract = web3WithAccount.Eth.GetContract(contractConfig.Abi, contractConfig.ContractAddress);
            var (function, parameters) = BuildFunctionCall(opportunity, contract);

            if (function == null || parameters == null)
            {
                return null;
            }

            var gasPriceWei = Web3.Convert.ToWei(gasBid.GasPriceGwei, Nethereum.Util.UnitConversion.EthUnit.Gwei);

            var transactionInput = function.CreateTransactionInput(
                account.Address,
                new HexBigInteger(gasBid.GasLimit),
                new HexBigInteger(gasPriceWei),
                new HexBigInteger(0),
                parameters
            );

            // Sign the transaction
            var signedTx = await account.TransactionManager.SignTransactionAsync(transactionInput);

            return new List<string> { signedTx };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build transactions for opportunity {Id}", opportunity.Id);
            return null;
        }
    }

    private (Function?, object[]?) BuildFunctionCall(FlashLoanOpportunity opportunity, Contract contract)
    {
        if (opportunity.Strategy == "CrossDex" && opportunity.SourceDex != null && opportunity.TargetDex != null)
        {
            var function = contract.GetFunction("executeCrossDexArbitrage");
            var parameters = new object[]
            {
                opportunity.Asset,
                Web3.Convert.ToWei(opportunity.Amount),
                opportunity.SourceDex,
                opportunity.TargetDex,
                Web3.Convert.ToWei(opportunity.MinProfit)
            };
            return (function, parameters);
        }
        
        if (opportunity.Strategy == "MultiHop" && opportunity.Path != null)
        {
            var function = contract.GetFunction("executeMultiHopArbitrage");
            var parameters = new object[]
            {
                opportunity.Asset,
                Web3.Convert.ToWei(opportunity.Amount),
                opportunity.Path.Split(','),
                Web3.Convert.ToWei(opportunity.MinProfit)
            };
            return (function, parameters);
        }
        
        if (opportunity.Strategy == "Triangular" && opportunity.Path != null)
        {
            var function = contract.GetFunction("executeTriangularArbitrage");
            var parameters = new object[]
            {
                opportunity.Asset,
                Web3.Convert.ToWei(opportunity.Amount),
                opportunity.Path.Split(','),
                Web3.Convert.ToWei(opportunity.MinProfit)
            };
            return (function, parameters);
        }
        
        if ((opportunity.Strategy == "JitoMEV" || opportunity.Strategy == "SuaveMEV") && 
            opportunity.SourceDex != null && opportunity.TargetDex != null)
        {
            // MEV strategies use the same cross-dex function but are routed through MEV providers
            var function = contract.GetFunction("executeCrossDexArbitrage");
            var parameters = new object[]
            {
                opportunity.Asset,
                Web3.Convert.ToWei(opportunity.Amount),
                opportunity.SourceDex,
                opportunity.TargetDex,
                Web3.Convert.ToWei(opportunity.MinProfit)
            };
            return (function, parameters);
        }

        _logger.LogWarning("Unknown strategy or missing parameters: {Strategy}", opportunity.Strategy);
        return (null, null);
    }

    private async Task<TransactionReceipt?> WaitForReceiptAsync(Web3 web3, string txHash)
    {
        int attempts = 0;
        const int maxAttempts = 60;

        while (attempts < maxAttempts)
        {
            await Task.Delay(2000);
            var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            if (receipt != null)
            {
                return receipt;
            }
            attempts++;
        }

        return null;
    }

    private async Task PublishEnhancedResultAsync(
        FlashLoanOpportunity opportunity,
        GasBid? gasBid,
        bool success,
        string? reason,
        string? txHash,
        long? blockNumber,
        decimal? estimatedProfit,
        decimal? actualProfit,
        long? gasUsed = null,
        string? gasPriceWei = null,
        decimal? actualCostUsd = null,
        string? mevProvider = null,
        string? bundleId = null,
        decimal? mevTipPaid = null,
        bool wasFrontrun = false,
        bool wasBackrun = false,
        string? blockHash = null,
        int? transactionIndex = null,
        long? confirmedAtNanos = null)
    {
        // Calculate slippage if we have both estimated and actual profit
        decimal? slippageBps = null;
        if (estimatedProfit.HasValue && actualProfit.HasValue && estimatedProfit.Value != 0)
        {
            slippageBps = ((estimatedProfit.Value - actualProfit.Value) / estimatedProfit.Value) * 10000;
        }

        var result = new FlashLoanResult(
            OpportunityId: opportunity.Id,
            ChainName: opportunity.ChainName,
            Strategy: opportunity.Strategy,
            Asset: opportunity.Asset,
            Amount: opportunity.Amount,
            Success: success,
            Reason: reason,
            TransactionHash: txHash,
            GasUsed: gasUsed,
            GasPrice: gasPriceWei,
            ActualCostUsd: actualCostUsd,
            Timestamp: DateTime.UtcNow,
            
            // Timing metrics
            OpportunityReceivedAtNanos: _opportunityReceivedAt,
            SimulationStartedAtNanos: _simulationStartedAt,
            SimulationCompletedAtNanos: _simulationCompletedAt,
            TransactionSubmittedAtNanos: _transactionSubmittedAt,
            TransactionConfirmedAtNanos: confirmedAtNanos,
            
            // Profit metrics
            EstimatedProfitUsd: estimatedProfit,
            ActualProfitUsd: actualProfit,
            SlippageBps: slippageBps,
            
            // Market context from opportunity
            SpreadBps: opportunity.SpreadBps,
            OrderBookImbalance: opportunity.OrderBookImbalance,
            VolatilityPercent: opportunity.VolatilityPercent,
            
            // MEV metrics
            MevProvider: mevProvider,
            BundleId: bundleId,
            BundlePosition: null,
            MevTipPaid: mevTipPaid,
            WasFrontrun: wasFrontrun,
            WasBackrun: wasBackrun,
            
            // Source info
            SourceProtocol: opportunity.SourceDex,
            TargetProtocol: opportunity.TargetDex,
            LiquidityProvider: opportunity.LiquidityProvider,
            PoolLiquidity: null,
            
            // Block info
            BlockNumber: blockNumber,
            BlockHash: blockHash,
            TransactionIndex: transactionIndex
        );

        await _resultPublisher.PublishResultAsync(result);

        // Also publish final status update
        await _resultPublisher.PublishStatusUpdateAsync(new FlashLoanStatusUpdate(
            opportunity.Id,
            success ? "confirmed" : "failed",
            DateTime.UtcNow,
            success ? txHash : reason
        ));
    }
}

public class SmartContractConfig
{
    public string ChainName { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;
    public string Abi { get; set; } = string.Empty;
}

public class GasBid
{
    public decimal GasPriceGwei { get; set; }
    public long GasLimit { get; set; }
    public decimal EstimatedCostUsd { get; set; }
}
