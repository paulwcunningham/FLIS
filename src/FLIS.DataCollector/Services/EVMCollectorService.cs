using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Web3;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using FLIS.DataCollector.Models;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace FLIS.DataCollector.Services;

/// <summary>
/// Background service that collects flash loan transactions from EVM chains
/// </summary>
public sealed class EVMCollectorService : BackgroundService
{
    private readonly BlockchainConfig _blockchainConfig;
    private readonly FlashLoanContractsConfig _contractsConfig;
    private readonly CollectorSettings _settings;
    private readonly DatabaseService _database;
    private readonly ILogger<EVMCollectorService> _logger;

    // Track last processed block per chain
    private readonly Dictionary<int, BigInteger> _lastProcessedBlock = new();

    // Known flash loan event signatures
    private static readonly Dictionary<string, string> FlashLoanEventSignatures = new()
    {
        // Aave V3 FlashLoan event
        ["0xefefaba5e921573100900a3ad9cf29f222d995fb3b6045797eaea7521bd8d6f0"] = "AaveFlashLoan",
        // Balancer FlashLoan event
        ["0x0d7d75e01ab95780d3cd1c8ec0dd6c2ce19f7cae57e0f3d3e1bc1d1bd55ce879"] = "BalancerFlashLoan",
        // Uniswap V3 Flash event
        ["0xbdbdb71d7860376ba52b25a5028beea23581364a40522f6bcfb86bb1f2dca633"] = "UniswapFlash",
        // dYdX LogOperation
        ["0x11c3e4e90f4cc09e9e7b2f2e31d75b9b4d9f4de2c1d8b4ae7ea0e0f7c7d5c3b2"] = "dYdXOperation"
    };

    // ETH price in USD (should be fetched from oracle in production)
    private const decimal ETH_PRICE_USD = 3500m;

    public EVMCollectorService(
        IOptions<BlockchainConfig> blockchainConfig,
        IOptions<FlashLoanContractsConfig> contractsConfig,
        IOptions<CollectorSettings> settings,
        DatabaseService database,
        ILogger<EVMCollectorService> logger)
    {
        _blockchainConfig = blockchainConfig.Value;
        _contractsConfig = contractsConfig.Value;
        _settings = settings.Value;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.EnableEVMCollector)
        {
            _logger.LogInformation("[EVMCollector] Disabled by configuration");
            return;
        }

        _logger.LogInformation("[EVMCollector] Starting EVM chain data collection for {Count} chains",
            _blockchainConfig.EVMChains.Count);

        // Create tasks for each chain
        var chainTasks = _blockchainConfig.EVMChains
            .Select(chain => ProcessChainAsync(chain, stoppingToken))
            .ToList();

        await Task.WhenAll(chainTasks);
    }

    private async Task ProcessChainAsync(EVMChainConfig chain, CancellationToken ct)
    {
        _logger.LogInformation("[EVMCollector] Starting collector for {Chain} (ChainId: {ChainId})",
            chain.Name, chain.ChainId);

        var web3 = new Web3(chain.RpcUrl);
        var contracts = _contractsConfig.GetContractsForChain(chain.Name);
        var contractAddresses = contracts?.GetAllAddresses().ToHashSet() ?? new HashSet<string>();

        if (contractAddresses.Count == 0)
        {
            _logger.LogWarning("[EVMCollector] No contracts configured for {Chain}", chain.Name);
            return;
        }

        _logger.LogInformation("[EVMCollector] Monitoring {Count} contracts on {Chain}",
            contractAddresses.Count, chain.Name);

        // Get starting block
        try
        {
            var latestBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            _lastProcessedBlock[chain.ChainId] = latestBlock.Value - 10; // Start 10 blocks back
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EVMCollector] Failed to get latest block for {Chain}", chain.Name);
            return;
        }

        var pollingInterval = TimeSpan.FromSeconds(_settings.PollingIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessNewBlocksAsync(web3, chain, contractAddresses, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EVMCollector] Error processing blocks on {Chain}", chain.Name);
            }

            await Task.Delay(pollingInterval, ct);
        }
    }

    private async Task ProcessNewBlocksAsync(
        Web3 web3,
        EVMChainConfig chain,
        HashSet<string> contractAddresses,
        CancellationToken ct)
    {
        var latestBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        var lastProcessed = _lastProcessedBlock[chain.ChainId];

        if (latestBlock.Value <= lastProcessed)
            return;

        var blocksToProcess = Math.Min(
            (int)(latestBlock.Value - lastProcessed),
            _settings.MaxBlocksPerBatch);

        _logger.LogDebug("[EVMCollector] {Chain}: Processing blocks {From} to {To}",
            chain.Name, lastProcessed + 1, lastProcessed + blocksToProcess);

        var flashLoanCount = 0;

        for (var i = 1; i <= blocksToProcess; i++)
        {
            var blockNumber = new HexBigInteger(lastProcessed + i);

            try
            {
                var block = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber
                    .SendRequestAsync(blockNumber);

                if (block?.Transactions == null) continue;

                var blockTimestamp = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value);

                foreach (var tx in block.Transactions)
                {
                    if (string.IsNullOrEmpty(tx.To)) continue;

                    var toAddressLower = tx.To.ToLowerInvariant();

                    // Check if transaction interacts with a flash loan contract
                    if (contractAddresses.Contains(toAddressLower))
                    {
                        var flashLoanTx = await ProcessPotentialFlashLoanAsync(
                            web3, chain, tx, blockTimestamp, ct);

                        if (flashLoanTx != null)
                        {
                            _database.QueueTransaction(flashLoanTx);
                            flashLoanCount++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EVMCollector] Error processing block {Block} on {Chain}",
                    blockNumber.Value, chain.Name);
            }
        }

        _lastProcessedBlock[chain.ChainId] = lastProcessed + blocksToProcess;

        if (flashLoanCount > 0)
        {
            _logger.LogInformation(
                "[EVMCollector] {Chain}: Found {Count} flash loan transactions in blocks {From}-{To}",
                chain.Name, flashLoanCount, lastProcessed + 1, lastProcessed + blocksToProcess);
        }
    }

    private async Task<FlashLoanTransaction?> ProcessPotentialFlashLoanAsync(
        Web3 web3,
        EVMChainConfig chain,
        Transaction tx,
        DateTimeOffset blockTimestamp,
        CancellationToken ct)
    {
        try
        {
            // Get transaction receipt for logs
            var receipt = await web3.Eth.Transactions.GetTransactionReceipt
                .SendRequestAsync(tx.TransactionHash);

            if (receipt?.Logs == null || receipt.Logs.Count == 0)
                return null;

            // Check if transaction was successful
            if (receipt.Status?.Value != 1)
                return null;

            // Look for flash loan events
            var flashLoanEvents = new List<FlashLoanEvent>();
            var actions = new List<FlashLoanAction>();
            string? protocol = null;
            decimal borrowedAmount = 0;
            string profitToken = "ETH";

            foreach (var logToken in receipt.Logs)
            {
                // Parse JToken log into structured format
                var log = logToken as JObject;
                if (log == null) continue;

                var topics = log["topics"] as JArray;
                if (topics == null || topics.Count == 0) continue;

                var eventSignature = topics[0]?.ToString();
                if (string.IsNullOrEmpty(eventSignature)) continue;

                if (FlashLoanEventSignatures.TryGetValue(eventSignature, out var eventName))
                {
                    protocol = eventName;

                    // Parse event data based on protocol
                    var eventData = ParseFlashLoanEvent(eventName, log);
                    if (eventData != null)
                    {
                        flashLoanEvents.Add(eventData);
                        borrowedAmount += eventData.Parameters.GetValueOrDefault("amount") as decimal? ?? 0;
                    }
                }

                // Track swap events for action sequence
                if (IsSwapEvent(eventSignature))
                {
                    actions.Add(new FlashLoanAction
                    {
                        ActionType = "swap",
                        OrderIndex = actions.Count
                    });
                }
            }

            // If no flash loan events found, this isn't a flash loan
            if (flashLoanEvents.Count == 0)
                return null;

            // Calculate gas costs
            var gasUsed = (long)receipt.GasUsed.Value;
            var gasPriceGwei = Web3.Convert.FromWei(tx.GasPrice.Value, Nethereum.Util.UnitConversion.EthUnit.Gwei);
            var gasCostEth = Web3.Convert.FromWei(receipt.GasUsed.Value * tx.GasPrice.Value);
            var gasCostUsd = gasCostEth * ETH_PRICE_USD;

            // Estimate profit (simplified - would need full trace in production)
            var estimatedProfit = EstimateProfit(receipt, tx.Value?.Value ?? 0);
            var netProfit = estimatedProfit - gasCostUsd;
            var isProfitable = netProfit > 0;

            // Add borrow and repay actions
            actions.Insert(0, new FlashLoanAction
            {
                ActionType = "borrow",
                Amount = borrowedAmount,
                Protocol = protocol,
                OrderIndex = 0
            });

            actions.Add(new FlashLoanAction
            {
                ActionType = "repay",
                Amount = borrowedAmount,
                Protocol = protocol,
                OrderIndex = actions.Count
            });

            return new FlashLoanTransaction
            {
                TxHash = tx.TransactionHash,
                BlockNumber = (long)tx.BlockNumber.Value,
                Timestamp = blockTimestamp,
                ChainId = chain.ChainId,
                Protocol = protocol ?? "Unknown",
                ContractAddress = tx.To,
                Actions = actions,
                ProfitToken = profitToken,
                ProfitAmount = netProfit,
                GasUsed = gasUsed,
                GasPriceGwei = gasPriceGwei,
                EffectiveGasCostUsd = gasCostUsd,
                IsProfitable = isProfitable
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EVMCollector] Error processing transaction {TxHash}", tx.TransactionHash);
            return null;
        }
    }

    private FlashLoanEvent? ParseFlashLoanEvent(string eventName, JObject log)
    {
        try
        {
            var address = log["address"]?.ToString() ?? "";
            var logIndex = log["logIndex"]?.Value<int>() ?? 0;
            var data = log["data"]?.ToString() ?? "";
            var topics = log["topics"] as JArray;

            return new FlashLoanEvent
            {
                EventName = eventName,
                ContractAddress = address,
                LogIndex = logIndex,
                Parameters = new Dictionary<string, object>
                {
                    ["raw_data"] = data,
                    ["topics_count"] = topics?.Count ?? 0
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private bool IsSwapEvent(string eventSignature)
    {
        // Common swap event signatures
        var swapSignatures = new HashSet<string>
        {
            "0xd78ad95fa46c994b6551d0da85fc275fe613ce37657fb8d5e3d130840159d822", // Uniswap V2 Swap
            "0xc42079f94a6350d7e6235f29174924f928cc2ac818eb64fed8004e115fbcca67", // Uniswap V3 Swap
            "0x2170c741c41531aec20e7c107c24eecfdd15e69c9bb0a8dd37b1840b9e0b207b"  // Balancer Swap
        };

        return swapSignatures.Contains(eventSignature);
    }

    private decimal EstimateProfit(TransactionReceipt receipt, BigInteger txValue)
    {
        // Simplified profit estimation
        // In production, would need to trace token transfers and calculate actual P&L
        // For now, use a heuristic based on transaction success and gas usage

        if (receipt.Status?.Value != 1)
            return 0;

        // Higher gas usage often indicates more complex (potentially profitable) arbitrage
        var gasUsed = (decimal)receipt.GasUsed.Value;

        // Estimate based on typical flash loan profit margins
        // This is a placeholder - real implementation would trace transfers
        var estimatedProfitUsd = gasUsed > 500000
            ? (gasUsed / 1000000) * 50  // Complex transaction, potentially higher profit
            : (gasUsed / 1000000) * 10; // Simpler transaction

        return estimatedProfitUsd;
    }
}
